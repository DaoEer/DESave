using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MemoryPack;
using UnityEngine;

namespace DESave
{
    public class SaveModuleRegistry
    {
        private readonly Dictionary<Type, ISaveModule> _instances = new();
        private readonly Dictionary<Type, FieldInfo[]> _cachedFields = new();

        public SaveModuleRegistry(Assembly targetAssembly)
        {
            Discover(targetAssembly);
        }

        private void Discover(Assembly assembly)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(ISaveModule).IsAssignableFrom(t))
                .OrderBy(t => t.FullName);

            foreach (var type in types)
            {
                var instance = (ISaveModule)Activator.CreateInstance(type);
                _instances[type] = instance;

                _cachedFields[type] = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(f => f.MetadataToken)
                    .ToArray();

                Debug.Log($"[DESave] 发现存档模块: {type.FullName}");
            }
        }

        public static int ComputeSchemaHash(Type moduleType)
        {
            unchecked
            {
                var hash = (int)2166136261;
                var fields = moduleType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(f => f.MetadataToken);

                foreach (var field in fields)
                {
                    var signature = field.Name + ":" + field.FieldType.FullName;
                    foreach (var c in signature)
                    {
                        hash ^= c;
                        hash *= 16777619;
                    }
                }

                return hash;
            }
        }

        public (SaveData saveData, bool hasErrors) CollectAll()
        {
            var saveData = new SaveData();
            var hasErrors = false;

            foreach (var (type, module) in _instances)
            {
                try
                {
                    var data = Collect(module);
                    if (data == null)
                    {
                        Debug.LogError($"[DESave] 模块 {type.Name} 返回了空数据");
                        hasErrors = true;
                        continue;
                    }

                    saveData.Modules[type.Name] = data;
                    var schemaHash = ComputeSchemaHash(type);
                    saveData.Modules[type.Name + ".SchemaHash"] = BitConverter.GetBytes(schemaHash);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[DESave] 从模块 {type.Name} 收集数据失败: {e.Message}");
                    hasErrors = true;
                }
            }

            return (saveData, hasErrors);
        }

        public T Get<T>() where T : class, ISaveModule
        {
            var type = typeof(T);

            if (_instances.TryGetValue(type, out var module))
            {
                return (T)module;
            }

            var instance = (T)Activator.CreateInstance(type);
            _instances[type] = instance;

            if (!_cachedFields.ContainsKey(type))
            {
                _cachedFields[type] = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(f => f.MetadataToken)
                    .ToArray();
            }

            return instance;
        }

        public ISaveModule FindByName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            foreach (var kvp in _instances)
            {
                if (kvp.Key.Name == typeName || kvp.Key.FullName == typeName)
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        public string GetTypeName(ISaveModule module)
        {
            return module.GetType().Name;
        }

        internal byte[] Collect(ISaveModule module)
        {
            var type = module.GetType();
            return MemoryPackSerializer.Serialize(type, module);
        }

        // 先反序列化到临时对象，再逐字段拷贝回单例，避免替换模块引用。
        internal void Restore(ISaveModule module, byte[] data)
        {
            var type = module.GetType();
            var deserialized = MemoryPackSerializer.Deserialize(type, data);

            if (deserialized == null || !_cachedFields.TryGetValue(type, out var fields))
            {
                return;
            }

            foreach (var field in fields)
            {
                var value = field.GetValue(deserialized);
                field.SetValue(module, value);
            }
        }
    }
}