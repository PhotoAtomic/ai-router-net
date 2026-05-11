using AiRouter.Protocol;

namespace AiRouter.Conversion;

public interface IResponseConverter
{
    string Convert(string body, ApiFormat from, ApiFormat to, ConversionContext? context = null);
}
