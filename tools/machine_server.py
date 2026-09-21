# -*- coding: utf-8 -*-
"""
Machine 官方/局域网服务器（独立进程，无需游戏）。
跑在任何接入 Radmin LAN / 局域网的机器上，作为 Machine Online 的服务器端。

用法：
    python machine_server.py [port]          # 默认 26460
    python machine_server.py 26460 --debug   # 打印消息

协议（与 Machine.Core.Net 一致，UTF-8 文本行 '\n' 结尾，字段 '|' 分隔）：
    C->S: LIST | HOST|code|name | JOIN|code|name | LEAVE | STATE|payload | PING
    S->C: ROOMS|list | HOSTED|code|pid | JOINED|code|pid | PEERS|list
          STATE|pid|payload | ERR|msg | KICK|msg | PONG

房间号：7 位数字字母（客户端 HOST 可自带，否则服务器随机生成）。
"""
import socket
import threading
import sys
import time
import random

PORT = 26460
DEBUG = False
MAX_ROOM = 8
ROOM_TTL = 300.0  # 房间 5 分钟无活动自动清理

class Room:
    def __init__(self, code, name, host_id):
        self.code = code
        self.name = name
        self.host_id = host_id
        self.players = []  # list of Conn
        self.last_active = time.time()

class Conn:
    _pid_counter = 1
    _pid_lock = threading.Lock()

    def __init__(self, sock, addr, server):
        self.sock = sock
        self.addr = addr
        self.server = server
        with Conn._pid_lock:
            self.pid = "P%d" % Conn._pid_counter
            Conn._pid_counter += 1
        self.name = ""
        self.room = None
        self.alive = True
        self.wlock = threading.Lock()

    def send(self, line):
        if not self.alive:
            return
        try:
            with self.wlock:
                self.sock.sendall((line + "\n").encode("utf-8"))
        except Exception:
            pass

    def close(self):
        self.alive = False
        try:
            self.sock.close()
        except Exception:
            pass

class Server:
    def __init__(self, port):
        self.port = port
        self.rooms = {}
        self.lock = threading.Lock()
        self.conns = []

    def start(self):
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", self.port))
        srv.listen(32)
        print("[MachineServer] listening on 0.0.0.0:%d" % self.port)
        threading.Thread(target=self._cleanup_loop, daemon=True).start()
        while True:
            try:
                sock, addr = srv.accept()
                c = Conn(sock, addr, self)
                with self.lock:
                    self.conns.append(c)
                threading.Thread(target=self._read_loop, args=(c,), daemon=True).start()
            except Exception as e:
                print("[MachineServer] accept error:", e)
                break

    def _read_loop(self, c):
        buf = b""
        try:
            while c.alive:
                data = c.sock.recv(8192)
                if not data:
                    break
                buf += data
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    msg = line.decode("utf-8", "replace").strip("\r")
                    if msg:
                        if DEBUG:
                            print("[%s ->] %s" % (c.pid, msg[:120]))
                        self.handle(c, msg)
        except Exception:
            pass
        finally:
            c.alive = False
            with self.lock:
                if c in self.conns:
                    self.conns.remove(c)
            self.remove_from_room(c)
            c.close()

    def handle(self, c, line):
        f = line.split("|")
        t = f[0]
        if t == "LIST":
            self.send_rooms(c)
        elif t == "HOST":
            code = f[1].upper() if len(f) > 1 and f[1] else self.new_code()
            if not self.valid_code(code):
                c.send("ERR|INVALID ROOM CODE")
                return
            with self.lock:
                if code in self.rooms:
                    c.send("ERR|ROOM EXISTS")
                    return
                name = f[2] if len(f) > 2 and f[2] else "Pilot"
                c.name = name
                room = Room(code, name, c.pid)
                room.players.append(c)
                self.rooms[code] = room
            c.room = self.rooms[code]
            c.send("HOSTED|%s|%s" % (code, c.pid))
            self.broadcast_peers(c.room)
        elif t == "JOIN":
            code = f[1].upper() if len(f) > 1 else ""
            if len(f) > 2 and f[2]:
                c.name = f[2]
            with self.lock:
                room = self.rooms.get(code)
                if room is None:
                    c.send("ERR|ROOM NOT FOUND")
                    return
                if len(room.players) >= MAX_ROOM:
                    c.send("ERR|ROOM FULL")
                    return
                room.players.append(c)
                room.last_active = time.time()
            c.room = room
            c.send("JOINED|%s|%s" % (room.code, c.pid))
            self.broadcast_peers(room)
        elif t == "LEAVE":
            self.remove_from_room(c)
        elif t == "STATE":
            room = c.room
            if room is None:
                return
            room.last_active = time.time()
            if len(f) > 1:
                self.broadcast_state(room, c.pid, f[1])
        elif t == "PING":
            c.send("PONG")
        self.cleanup_rooms()

    def send_rooms(self, c):
        parts = []
        with self.lock:
            for code, r in self.rooms.items():
                parts.append("%s:%s:%d:%d" % (r.code, r.name, len(r.players), MAX_ROOM))
        c.send("ROOMS|%s|%d" % (";".join(parts), len(parts)))

    def broadcast_peers(self, room):
        if room is None:
            return
        parts = []
        with self.lock:
            for p in room.players:
                parts.append("%s:%s" % (p.pid, p.name or "Pilot"))
        line = "PEERS|%s|%d" % (";".join(parts), len(parts))
        for p in list(room.players):
            p.send(line)

    def broadcast_state(self, room, from_pid, payload):
        for p in list(room.players):
            if p.pid != from_pid:
                p.send("STATE|%s|%s" % (from_pid, payload))

    def remove_from_room(self, c):
        with self.lock:
            room = c.room
            if room is None:
                return
            if c in room.players:
                room.players.remove(c)
            if room.host_id == c.pid:
                # 房主离开：房间解散
                self.rooms.pop(room.code, None)
                for p in list(room.players):
                    p.send("KICK|HOST LEFT")
                room.players = []
            else:
                self.broadcast_peers(room)
            c.room = None

    def cleanup_rooms(self):
        now = time.time()
        with self.lock:
            dead = [code for code, r in self.rooms.items() if now - r.last_active > ROOM_TTL]
            for code in dead:
                r = self.rooms.pop(code, None)
                if r:
                    for p in list(r.players):
                        p.send("KICK|ROOM CLOSED")
                    r.players = []

    def _cleanup_loop(self):
        while True:
            time.sleep(30)
            self.cleanup_rooms()

    @staticmethod
    def new_code():
        chars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
        return "".join(random.choice(chars) for _ in range(7))

    @staticmethod
    def valid_code(c):
        if not c or len(c) > 16:
            return False
        return all(ch.isalnum() or ch == "-" for ch in c)


if __name__ == "__main__":
    if len(sys.argv) > 1:
        try:
            PORT = int(sys.argv[1])
        except ValueError:
            pass
    if "--debug" in sys.argv:
        DEBUG = True
    Server(PORT).start()
