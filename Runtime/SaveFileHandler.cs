using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using MemoryPack;
using UnityEngine;

namespace DESave
{
    public class SaveFileHandler
    {
        private const string RecordFileName = "save_record.mp";
        private const string SaveFileExtension = ".mp";

        private readonly string _saveDirectory;
        private Dictionary<long, SaveMetaData> _saveRecord;

        public SaveFileHandler(string saveDirectory)
        {
            _saveDirectory = saveDirectory;
            LoadRecord();
            Debug.Log($"[DESave] 文件处理器已初始化，共 {_saveRecord.Count} 个存档");
        }

        public bool SaveExists(long saveId) => _saveRecord.ContainsKey(saveId);

        public SaveMetaData GetMetaData(long saveId)
        {
            return _saveRecord.TryGetValue(saveId, out var meta) ? CloneMeta(meta) : null;
        }

        public List<(long SaveId, SaveMetaData Meta)> ListSaves()
        {
            var result = new List<(long, SaveMetaData)>(_saveRecord.Count);
            foreach (var kvp in _saveRecord)
                result.Add((kvp.Key, CloneMeta(kvp.Value)));
            return result;
        }

        public long CreateSave(string saveName)
        {
            if (string.IsNullOrEmpty(saveName))
            {
                Debug.LogError("[DESave] 存档名称不能为空");
                return -1;
            }

            var saveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000
                         + (long)(UnityEngine.Random.value * 9999);

            if (_saveRecord.ContainsKey(saveId))
            {
                Debug.LogWarning($"[DESave] 存档 ID 冲突: {saveId}");
                return -1;
            }

            var meta = new SaveMetaData
            {
                SaveName = saveName,
                SaveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                PlayTimeSeconds = 0,
                SaveVersion = SaveMetaData.SAVE_VERSION
            };

            _saveRecord[saveId] = meta;

            try
            {
                var data = MemoryPackSerializer.Serialize(new SaveData());
                var crc = Crc32.Compute(data);
                var content = new byte[4 + data.Length];
                Crc32.Pack(crc, content);
                Buffer.BlockCopy(data, 0, content, 4, data.Length);
                File.WriteAllBytes(GetFilePath(saveId), content);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 创建存档文件失败: {e.Message}");
                return -1;
            }

            SaveRecord();
            Debug.Log($"[DESave] 已创建存档: {saveName} (ID: {saveId})");
            return saveId;
        }

        public async UniTask<bool> WriteSaveAsync(long saveId, SaveData data)
        {
            if (!_saveRecord.TryGetValue(saveId, out var meta))
            {
                Debug.LogError($"[DESave] 存档不存在: {saveId}");
                return false;
            }

            try
            {
                var serialized = MemoryPackSerializer.Serialize(data);
                var crc = Crc32.Compute(serialized);
                var content = new byte[4 + serialized.Length];
                Crc32.Pack(crc, content);
                Buffer.BlockCopy(serialized, 0, content, 4, serialized.Length);

                var filePath = GetFilePath(saveId);
                await WriteFileSafeAsync(filePath, content);

                meta.SaveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveRecord();

                Debug.Log($"[DESave] 保存成功: {saveId} (大小: {serialized.Length} bytes)");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 保存失败: {e.Message}");
                return false;
            }
        }

        public async UniTask<(bool success, SaveData data)> ReadSaveAsync(long saveId)
        {
            if (!_saveRecord.TryGetValue(saveId, out var meta))
            {
                Debug.LogError($"[DESave] 存档不存在: {saveId}");
                return (false, null);
            }

            var filePath = GetFilePath(saveId);

            if (!File.Exists(filePath))
            {
                var bakPath = filePath + ".bak";
                if (File.Exists(bakPath))
                {
                    Debug.LogWarning($"[DESave] 文件丢失，尝试备份恢复: {saveId}");
                    File.Move(bakPath, filePath);
                }
                else
                {
                    Debug.LogError($"[DESave] 文件不存在: {filePath}");
                    return (false, null);
                }
            }

            try
            {
                var content = await ReadFileAsync(filePath);
                if (content == null || content.Length < 4)
                {
                    Debug.LogError($"[DESave] 文件数据不足: {filePath}");
                    return (false, null);
                }

                if (meta.SaveVersion != SaveMetaData.SAVE_VERSION)
                {
                    Debug.LogError($"[DESave] 版本不匹配: 当前={SaveMetaData.SAVE_VERSION}, 文件={meta.SaveVersion}");
                    return (false, null);
                }

                byte[] serialized;

                var storedCrc = Crc32.Unpack(content);
                serialized = new byte[content.Length - 4];
                Buffer.BlockCopy(content, 4, serialized, 0, serialized.Length);

                if (Crc32.Compute(serialized) != storedCrc)
                {
                    Debug.LogWarning($"[DESave] CRC 校验失败: {saveId}，尝试备份");

                    var bakPath = filePath + ".bak";
                    if (File.Exists(bakPath))
                    {
                        var bakContent = await ReadFileAsync(bakPath);
                        if (bakContent != null && bakContent.Length > 4)
                        {
                            var bakCrc = Crc32.Unpack(bakContent);
                            var bakData = new byte[bakContent.Length - 4];
                            Buffer.BlockCopy(bakContent, 4, bakData, 0, bakData.Length);

                            if (Crc32.Compute(bakData) == bakCrc)
                            {
                                serialized = bakData;
                                File.Copy(bakPath, filePath, true);
                                Debug.Log($"[DESave] 已从备份恢复: {saveId}");
                            }
                        }
                    }
                }

                var data = MemoryPackSerializer.Deserialize<SaveData>(serialized);
                if (data != null)
                {
                    meta.SaveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    SaveRecord();
                    Debug.Log($"[DESave] 加载成功: {saveId} (大小: {serialized.Length} bytes)");
                    return (true, data);
                }

                Debug.LogError("[DESave] 反序列化失败");
                return (false, null);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 加载失败: {e.Message}");
                return (false, null);
            }
        }

        public bool DeleteSave(long saveId)
        {
            if (!_saveRecord.ContainsKey(saveId))
            {
                return false;
            }

            try
            {
                var filePath = GetFilePath(saveId);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                var bakPath = filePath + ".bak";
                if (File.Exists(bakPath))
                {
                    File.Delete(bakPath);
                }

                _saveRecord.Remove(saveId);
                SaveRecord();
                Debug.Log($"[DESave] 已删除存档: {saveId}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 删除失败: {e.Message}");
                return false;
            }
        }

        private void LoadRecord()
        {
            try
            {
                var recordPath = GetRecordPath();
                if (!File.Exists(recordPath))
                {
                    _saveRecord = new Dictionary<long, SaveMetaData>();
                    return;
                }

                var data = File.ReadAllBytes(recordPath);
                if (data.Length == 0)
                {
                    _saveRecord = new Dictionary<long, SaveMetaData>();
                    return;
                }

                _saveRecord = MemoryPackSerializer.Deserialize<Dictionary<long, SaveMetaData>>(data)
                              ?? new Dictionary<long, SaveMetaData>();
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 加载记录文件失败: {e.Message}");
                _saveRecord = new Dictionary<long, SaveMetaData>();
            }
        }

        private void SaveRecord()
        {
            try
            {
                _saveRecord ??= new Dictionary<long, SaveMetaData>();
                var data = MemoryPackSerializer.Serialize(_saveRecord);
                var path = GetRecordPath();
                var tmpPath = path + ".tmp";

                File.WriteAllBytes(tmpPath, data);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tmpPath, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 保存记录文件失败: {e.Message}");
            }
        }

        private string GetFilePath(long saveId)
        {
            return Path.Combine(_saveDirectory, $"{saveId}{SaveFileExtension}");
        }

        private string GetRecordPath()
        {
            return Path.Combine(_saveDirectory, RecordFileName);
        }

        private static SaveMetaData CloneMeta(SaveMetaData src)
        {
            return new SaveMetaData
            {
                SaveName = src.SaveName,
                SaveTimestamp = src.SaveTimestamp,
                PlayTimeSeconds = src.PlayTimeSeconds,
                SaveVersion = src.SaveVersion
            };
        }

        private async UniTask<byte[]> ReadFileAsync(string path)
        {
            try
            {
                return await UniTask.RunOnThreadPool(() => File.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 读取文件失败: {path}, {e.Message}");
                return null;
            }
        }

        // 崩溃安全写入：临时文件 → .bak 轮转 → 正式文件。
        private async UniTask WriteFileSafeAsync(string filePath, byte[] data)
        {
            var tmpPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bakPath = filePath + ".bak";

            try
            {
                await UniTask.RunOnThreadPool(() => File.WriteAllBytes(tmpPath, data));

                if (File.Exists(filePath))
                {
                    if (File.Exists(bakPath))
                    {
                        File.Delete(bakPath);
                    }

                    File.Move(filePath, bakPath);
                }

                File.Move(tmpPath, filePath);

                if (File.Exists(bakPath))
                {
                    File.Delete(bakPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 写入文件失败: {filePath}, {e.Message}");

                if (!File.Exists(filePath) && File.Exists(bakPath))
                {
                    File.Move(bakPath, filePath);
                    Debug.Log($"[DESave] 已从备份恢复: {filePath}");
                }

                throw;
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
        }

        // 同步崩溃安全写入。
        private void WriteFileSafeSync(string filePath, byte[] data)
        {
            var tmpPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bakPath = filePath + ".bak";

            try
            {
                File.WriteAllBytes(tmpPath, data);

                if (File.Exists(filePath))
                {
                    if (File.Exists(bakPath))
                    {
                        File.Delete(bakPath);
                    }

                    File.Move(filePath, bakPath);
                }

                File.Move(tmpPath, filePath);

                if (File.Exists(bakPath))
                {
                    File.Delete(bakPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 写入文件失败: {filePath}, {e.Message}");

                if (!File.Exists(filePath) && File.Exists(bakPath))
                {
                    File.Move(bakPath, filePath);
                    Debug.Log($"[DESave] 已从备份恢复: {filePath}");
                }

                throw;
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
        }

        // 用于 Editor 退出 / 应用暂停等必须立即完成的场景。
        public bool WriteSaveSync(long saveId, SaveData data)
        {
            if (!_saveRecord.TryGetValue(saveId, out var meta))
            {
                Debug.LogError($"[DESave] 存档不存在: {saveId}");
                return false;
            }

            try
            {
                var serialized = MemoryPackSerializer.Serialize(data);
                var crc = Crc32.Compute(serialized);
                var content = new byte[4 + serialized.Length];
                Crc32.Pack(crc, content);
                Buffer.BlockCopy(serialized, 0, content, 4, serialized.Length);

                var filePath = GetFilePath(saveId);
                WriteFileSafeSync(filePath, content);

                meta.SaveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveRecord();

                Debug.Log($"[DESave] 同步保存成功: {saveId} (大小: {serialized.Length} bytes)");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DESave] 同步保存失败: {e.Message}");
                return false;
            }
        }
    }

    // 不依赖 System.IO.Hashing 的自有 CRC32 实现。
    internal static class Crc32
    {
        private static readonly uint[] Table = GenerateTable();

        private static uint[] GenerateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                    {
                        crc = (crc >> 1) ^ 0xEDB88320;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }

                table[i] = crc;
            }

            return table;
        }

        public static uint Compute(byte[] data)
        {
            var crc = 0xFFFFFFFF;
            foreach (var b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        public static void Pack(uint crc, byte[] output)
        {
            output[0] = (byte)(crc & 0xFF);
            output[1] = (byte)((crc >> 8) & 0xFF);
            output[2] = (byte)((crc >> 16) & 0xFF);
            output[3] = (byte)((crc >> 24) & 0xFF);
        }

        public static uint Unpack(byte[] header)
        {
            return (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
        }
    }
}