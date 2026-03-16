using System.Threading.Tasks;

namespace BadmintonFYP.Api.Services
{
    public interface IWhatsAppService
    {
        Task SendMessageAsync(string toPhoneNumber, string message, string mediaUrl = null);
    }
}
