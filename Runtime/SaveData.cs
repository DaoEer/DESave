using System.Collections.Generic;
using MemoryPack;

namespace DESave
{
    [MemoryPackable]
    public partial class SaveData
    {
        public Dictionary<string, byte[]> Modules = new();
    }

    // 存档列表摘要专用，避免反序列化完整 SaveData。
    [MemoryPackable]
    public partial class SaveMetaData
    {
        public const int SAVE_VERSION = 1;

        public string SaveName;
        public long SaveTimestamp;
        public long PlayTimeSeconds;
        public int SaveVersion;
    }
}