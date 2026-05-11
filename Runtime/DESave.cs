using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace DESave
{
    public static class DESave
    {
        private static SaveFileHandler _fileHandler;
        private static SaveModuleRegistry _moduleRegistry;
        private static Dictionary<string, byte[]> _pendingRestore;
        private static bool _initialized;

        public static bool IsInitialized => _initialized;

        public static long? CurrentSaveId { get; set; }

        public static void Initialize(Assembly targetAssembly, string saveDirectory = null)
        {
            if (_initialized)
            {
                Debug.LogWarning("[DESave] 存档系统已初始化，跳过重复初始化");
                return;
            }

            try
            {
                var dir = saveDirectory ?? Path.Combine(Application.persistentDataPath, "Saves");
                _fileHandler = new SaveFileHandler(dir);
                _moduleRegistry = new SaveModuleRegistry(targetAssembly);
                _pendingRestore = new Dictionary<string, byte[]>();
                _initialized = true;

                Debug.Log("[DESave] 存档系统初始化完成");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DESave] 初始化失败: {ex.Message}");
                throw;
            }
        }

        public static void Cleanup()
        {
            if (!_initialized)
            {
                return;
            }

            _fileHandler = null;
            _moduleRegistry = null;
            _pendingRestore = null;
            _initialized = false;

            Debug.Log("[DESave] 存档系统已清理");
        }

        public static T Get<T>() where T : class, ISaveModule
        {
            if (!_initialized)
            {
                Debug.LogError("[DESave] 未初始化，无法获取模块");
                return null;
            }

            var adapter = _moduleRegistry.Get<T>();
            var type = typeof(T);
            var typeName = type.Name;

            if (_pendingRestore != null && _pendingRestore.TryGetValue(typeName, out var data))
            {
                var hashKey = typeName + ".SchemaHash";
                if (!_pendingRestore.TryGetValue(hashKey, out var hashBytes) || hashBytes.Length < 4)
                {
                    Debug.LogWarning($"[DESave] 模块 {typeName} 缺少 SchemaHash（旧格式），跳过恢复，使用默认值");
                    _pendingRestore.Remove(typeName);
                    return adapter;
                }

                var storedHash = BitConverter.ToInt32(hashBytes, 0);
                var currentHash = SaveModuleRegistry.ComputeSchemaHash(type);
                if (storedHash != currentHash)
                {
                    Debug.LogWarning($"[DESave] 模块 {typeName} 字段结构已变更 (存:{storedHash:X8} 现:{currentHash:X8})，使用默认值");
                    _pendingRestore.Remove(typeName);
                    _pendingRestore.Remove(hashKey);
                    return adapter;
                }

                Debug.Log($"[DESave] 懒恢复模块: {typeName}");
                try
                {
                    _moduleRegistry.Restore(adapter, data);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DESave] 模块 {typeName} 恢复失败: {e.Message}");
                }

                _pendingRestore.Remove(typeName);
            }

            return adapter;
        }

        public static bool SaveSync(long saveId)
        {
            if (!_initialized)
            {
                Debug.LogError("[DESave] 未初始化，无法保存");
                return false;
            }

            var (saveData, hasErrors) = _moduleRegistry.CollectAll();
            if (hasErrors)
            {
                Debug.LogWarning("[DESave] 部分模块收集数据出错，仍将写入已收集的数据");
            }

            return _fileHandler.WriteSaveSync(saveId, saveData);
        }

        public static async UniTask<bool> SaveAsync(long saveId)
        {
            if (!_initialized)
            {
                Debug.LogError("[DESave] 未初始化，无法保存");
                return false;
            }

            var (saveData, hasErrors) = _moduleRegistry.CollectAll();
            if (hasErrors)
            {
                Debug.LogWarning($"[DESave] 部分模块收集数据出错，仍将写入已收集的数据");
            }

            return await _fileHandler.WriteSaveAsync(saveId, saveData);
        }

        public static async UniTask<bool> LoadAsync(long saveId)
        {
            if (!_initialized)
            {
                Debug.LogError("[DESave] 未初始化，无法加载");
                return false;
            }

            var (success, saveData) = await _fileHandler.ReadSaveAsync(saveId);
            if (!success || saveData == null)
            {
                return false;
            }

            _pendingRestore = saveData.Modules ?? new Dictionary<string, byte[]>();

            var hasFailure = TryRestoreAllPending();
            if (hasFailure)
            {
                Debug.LogWarning($"[DESave] 存档 {saveId} 部分模块结构已变更，将以新结构覆盖");
                await SaveAsync(saveId);
            }

            return true;
        }

        public static long CreateSave(string saveName)
        {
            if (!_initialized)
            {
                Debug.LogError("[DESave] 未初始化，无法创建存档");
                return -1;
            }

            return _fileHandler.CreateSave(saveName);
        }

        public static bool DeleteSave(long saveId)
        {
            if (!_initialized)
            {
                Debug.LogError("[DESave] 未初始化，无法删除存档");
                return false;
            }

            return _fileHandler.DeleteSave(saveId);
        }

        public static List<(long SaveId, SaveMetaData Meta)> ListSaves()
        {
            if (!_initialized)
            {
                return new List<(long, SaveMetaData)>();
            }

            return _fileHandler.ListSaves();
        }

        public static bool SaveExists(long saveId)
        {
            return _initialized && _fileHandler.SaveExists(saveId);
        }

        public static SaveMetaData GetMetaData(long saveId)
        {
            if (!_initialized)
            {
                return null;
            }

            return _fileHandler.GetMetaData(saveId);
        }

        private static bool TryRestoreAllPending()
        {
            if (_pendingRestore == null || _pendingRestore.Count == 0)
            {
                return false;
            }

            var hasFailure = false;
            var keys = new List<string>(_pendingRestore.Keys);

            foreach (var typeName in keys)
            {
                if (!_pendingRestore.TryGetValue(typeName, out var data))
                {
                    continue;
                }

                if (typeName.EndsWith(".SchemaHash"))
                {
                    continue;
                }

                var adapter = _moduleRegistry.FindByName(typeName);
                if (adapter == null)
                {
                    _pendingRestore.Remove(typeName);
                    continue;
                }

                var adapterType = adapter.GetType();

                var hashKey = typeName + ".SchemaHash";
                if (!_pendingRestore.TryGetValue(hashKey, out var hashBytes) || hashBytes.Length < 4)
                {
                    Debug.LogWarning($"[DESave] 模块 {typeName} 缺少 SchemaHash，丢弃旧数据");
                    hasFailure = true;
                    _pendingRestore.Remove(typeName);
                    continue;
                }

                var storedHash = BitConverter.ToInt32(hashBytes, 0);
                var currentHash = SaveModuleRegistry.ComputeSchemaHash(adapterType);
                if (storedHash != currentHash)
                {
                    Debug.LogWarning($"[DESave] 模块 {typeName} 结构变更 (存:{storedHash:X8} 现:{currentHash:X8})，丢弃旧数据");
                    hasFailure = true;
                    _pendingRestore.Remove(typeName);
                    _pendingRestore.Remove(hashKey);
                    continue;
                }

                try
                {
                    _moduleRegistry.Restore(adapter, data);
                    Debug.Log($"[DESave] 模块数据恢复: {typeName}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DESave] 模块 {typeName} 恢复异常: {e.Message}");
                    hasFailure = true;
                }

                _pendingRestore.Remove(typeName);
            }

            return hasFailure;
        }
    }
}