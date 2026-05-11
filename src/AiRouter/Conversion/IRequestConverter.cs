using AiRouter.Protocol;

namespace AiRouter.Conversion;

public interface IRequestConverter
{
    (string body, ConversionContext context) Convert(string body, ApiFormat from, ApiFormat to);
}
