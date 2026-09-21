using System;
using System.Collections.Generic;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// 通用对象池。用于频繁创建/销毁的对象（导弹、爆炸特效、UI元素等），
    /// 减少GC压力和Instantiate开销。
    /// </summary>
    /// <typeparam name="T">对象类型，必须继承Component</typeparam>
    public class ObjectPool<T> where T : Component
    {
        private readonly Lifo<T> _pool = new Lifo<T>();
        private readonly T _prefab;
        private readonly Transform _parent;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private int _createdCount;
        private readonly int _maxSize;

        /// <summary>池中当前可用对象数。</summary>
        public int AvailableCount { get { return _pool.Count; } }

        /// <summary>已创建的对象总数。</summary>
        public int CreatedCount { get { return _createdCount; } }

        /// <summary>
        /// 创建对象池。
        /// </summary>
        /// <param name="prefab">预制体</param>
        /// <param name="parent">父对象（池化对象的容器）</param>
        /// <param name="onGet">获取对象时的回调（可选）</param>
        /// <param name="onRelease">归还对象时的回调（可选）</param>
        /// <param name="initialSize">初始预创建数量</param>
        /// <param name="maxSize">最大池大小（超过则销毁不归还）</param>
        public ObjectPool(T prefab, Transform parent = null,
                          Action<T> onGet = null, Action<T> onRelease = null,
                          int initialSize = 0, int maxSize = 100)
        {
            _prefab = prefab;
            _parent = parent;
            _onGet = onGet;
            _onRelease = onRelease;
            _maxSize = maxSize;

            // 预创建
            for (int i = 0; i < initialSize; i++)
            {
                T obj = CreateNew();
                obj.gameObject.SetActive(false);
                _pool.Push(obj);
            }
        }

        /// <summary>从池中获取对象。</summary>
        public T Get()
        {
            T obj;
            if (_pool.Count > 0)
            {
                obj = _pool.Pop();
            }
            else
            {
                obj = CreateNew();
            }

            obj.gameObject.SetActive(true);
            if (_onGet != null) _onGet(obj);
            return obj;
        }

        /// <summary>从池中获取对象并设置位置和旋转。</summary>
        public T Get(Vector3 position, Quaternion rotation)
        {
            T obj = Get();
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            return obj;
        }

        /// <summary>归还对象到池中。</summary>
        public void Release(T obj)
        {
            if (obj == null) return;

            if (_onRelease != null) _onRelease(obj);
            obj.gameObject.SetActive(false);

            if (_pool.Count < _maxSize)
            {
                _pool.Push(obj);
            }
            else
            {
                // 池已满，销毁对象
                UnityEngine.Object.Destroy(obj.gameObject);
            }
        }

        /// <summary>清空池，销毁所有对象。</summary>
        public void Clear()
        {
            while (_pool.Count > 0)
            {
                T obj = _pool.Pop();
                if (obj != null) UnityEngine.Object.Destroy(obj.gameObject);
            }
            _createdCount = 0;
        }

        private T CreateNew()
        {
            T obj;
            if (_parent != null)
                obj = UnityEngine.Object.Instantiate(_prefab, _parent);
            else
                obj = UnityEngine.Object.Instantiate(_prefab);

            obj.name = _prefab.name + "_pool_" + _createdCount;
            _createdCount++;
            return obj;
        }
    }

    /// <summary>
    /// GameObject对象池（不需要特定Component类型时使用）。
    /// </summary>
    public class GameObjectPool
    {
        private readonly Lifo<GameObject> _pool = new Lifo<GameObject>();
        private readonly GameObject _prefab;
        private readonly Transform _parent;
        private readonly Action<GameObject> _onGet;
        private readonly Action<GameObject> _onRelease;
        private int _createdCount;
        private readonly int _maxSize;

        public int AvailableCount { get { return _pool.Count; } }
        public int CreatedCount { get { return _createdCount; } }

        public GameObjectPool(GameObject prefab, Transform parent = null,
                              Action<GameObject> onGet = null, Action<GameObject> onRelease = null,
                              int initialSize = 0, int maxSize = 100)
        {
            _prefab = prefab;
            _parent = parent;
            _onGet = onGet;
            _onRelease = onRelease;
            _maxSize = maxSize;

            for (int i = 0; i < initialSize; i++)
            {
                GameObject obj = CreateNew();
                obj.SetActive(false);
                _pool.Push(obj);
            }
        }

        public GameObject Get()
        {
            GameObject obj;
            if (_pool.Count > 0)
                obj = _pool.Pop();
            else
                obj = CreateNew();

            obj.SetActive(true);
            if (_onGet != null) _onGet(obj);
            return obj;
        }

        public GameObject Get(Vector3 position, Quaternion rotation)
        {
            GameObject obj = Get();
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            return obj;
        }

        public void Release(GameObject obj)
        {
            if (obj == null) return;
            if (_onRelease != null) _onRelease(obj);
            obj.SetActive(false);

            if (_pool.Count < _maxSize)
                _pool.Push(obj);
            else
                UnityEngine.Object.Destroy(obj);
        }

        public void Clear()
        {
            while (_pool.Count > 0)
            {
                GameObject obj = _pool.Pop();
                if (obj != null) UnityEngine.Object.Destroy(obj);
            }
            _createdCount = 0;
        }

        private GameObject CreateNew()
        {
            GameObject obj;
            if (_parent != null)
                obj = UnityEngine.Object.Instantiate(_prefab, _parent);
            else
                obj = UnityEngine.Object.Instantiate(_prefab);

            obj.name = _prefab.name + "_pool_" + _createdCount;
            _createdCount++;
            return obj;
        }
    }

    /// <summary>
    /// 全局对象池管理器。Mod可以通过此管理器注册和获取命名对象池。
    /// </summary>
    public static class PoolManager
    {
        private static readonly Dictionary<string, object> _pools = new Dictionary<string, object>();
        private static GameObject _root;

        /// <summary>初始化池管理器（创建根容器）。</summary>
        public static void Init()
        {
            if (_root == null)
            {
                _root = new GameObject("Machine_ObjectPools");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
        }

        /// <summary>注册一个GameObject池。</summary>
        public static GameObjectPool RegisterPool(string name, GameObject prefab,
                                                    int initialSize = 0, int maxSize = 100)
        {
            Init();
            GameObjectPool pool = new GameObjectPool(prefab, _root.transform, null, null, initialSize, maxSize);
            _pools[name] = pool;
            return pool;
        }

        /// <summary>获取已注册的GameObject池。</summary>
        public static GameObjectPool GetPool(string name)
        {
            object pool;
            if (_pools.TryGetValue(name, out pool))
                return pool as GameObjectPool;
            return null;
        }

        /// <summary>从指定池中获取对象。</summary>
        public static GameObject Get(string poolName)
        {
            GameObjectPool pool = GetPool(poolName);
            if (pool != null) return pool.Get();
            return null;
        }

        /// <summary>归还对象到指定池。</summary>
        public static void Release(string poolName, GameObject obj)
        {
            GameObjectPool pool = GetPool(poolName);
            if (pool != null) pool.Release(obj);
        }

        /// <summary>清空所有池（场景切换时调用）。</summary>
        public static void ClearAll()
        {
            foreach (var kvp in _pools)
            {
                GameObjectPool pool = kvp.Value as GameObjectPool;
                if (pool != null) pool.Clear();
            }
            _pools.Clear();
        }

        /// <summary>获取池统计信息。</summary>
        public static string GetStats()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Object Pool Stats ===");
            foreach (var kvp in _pools)
            {
                GameObjectPool pool = kvp.Value as GameObjectPool;
                if (pool != null)
                {
                    sb.AppendLine(string.Format("  {0,-25} available:{1,4}  created:{2,4}",
                        kvp.Key, pool.AvailableCount, pool.CreatedCount));
                }
            }
            sb.AppendLine("  Total pools: " + _pools.Count);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 极简 LIFO 容器，API 与 Stack&lt;T&gt; 完全一致（Push / Pop / Count）。
    ///
    /// 为什么不用 System.Collections.Generic.Stack&lt;T&gt;：
    /// Machine.Core 编译时同时引用了 csc 自带的 System.dll（经由 csc 的响应文件）
    /// 和游戏自带的 mscorlib.dll，两者都定义了 Stack&lt;T&gt;。只要代码里用到它，
    /// csc 就报 CS0433「类型同时存在于两个程序集」——整个核心编不出来（2026-09-14 实测）。
    /// 换成 List&lt;T&gt; 实现即可绕开，行为完全等价。
    /// </summary>
    internal class Lifo<T>
    {
        private readonly List<T> _items = new List<T>();

        public int Count { get { return _items.Count; } }

        public void Push(T item) { _items.Add(item); }

        public T Pop()
        {
            int last = _items.Count - 1;
            T item = _items[last];
            _items.RemoveAt(last);
            return item;
        }
    }
}
