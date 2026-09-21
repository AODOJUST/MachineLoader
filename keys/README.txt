!!! DO NOT SHIP THIS DIRECTORY !!!

release_private.pem 是 Machine 更新通道的信任根。任何拿到它的人都能签出
"客户端会接受的"恶意 Machine.Core.dll。

- 绝不把 keys/ 复制进 dist/、发布仓库或任何分发包
- 泄露后必须立刻重新生成密钥对，并发布一个正常版本把新公钥推给客户端
- 备份建议放在离线介质或密码管理器里

release_public.pem 可以公开（dist/release_pubkey.pem 就是它的副本，
供用户用 openssl 离线核对签名）。

生成密钥对（一次性）：

  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out release_private.pem
  openssl rsa -in release_private.pem -pubout -out release_public.pem

签发一个版本：

  python pack_dist.py
  python tools/sign_release.py --set-version 2.4.0

离线核对签名：

  openssl dgst -sha256 -verify release_public.pem -signature sig.bin Machine.Core.dll
