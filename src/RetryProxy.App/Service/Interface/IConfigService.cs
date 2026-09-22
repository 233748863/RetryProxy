using RetryProxy.Core.Config;

namespace RetryProxy.Service.Interface
{
    public interface IConfigService
    {
        AllConfig Get();

        void Save();

        AllConfig Read();

        void Write(AllConfig config);
    }
}
