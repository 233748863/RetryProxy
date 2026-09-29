using RetryProxy.Core.Config;

namespace RetryProxy.Service.Interface
{
    public interface IConfigService
    {
        AllConfig Get();

        void Save();

        void SaveChecked();

        void BackupBeforeDeletion();

        void BackupBeforeClientTakeover();
    }
}
