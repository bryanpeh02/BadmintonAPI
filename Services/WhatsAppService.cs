using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;

namespace BadmintonFYP.Api.Services
{
    public class WhatsAppService : IWhatsAppService
    {
        private readonly string _accountSid;
        private readonly string _authToken;
        private readonly string _fromNumber;

        public WhatsAppService(IConfiguration configuration)
        {
            _accountSid = configuration["Twilio:AccountSid"];
            _authToken = configuration["Twilio:AuthToken"];
            _fromNumber = configuration["Twilio:FromWhatsAppNumber"];
        }

        public async Task SendMessageAsync(string toPhoneNumber, string message, string mediaUrl = null)
        {
            try
            {
                // Initialize Twilio client
                TwilioClient.Init(_accountSid, _authToken);

                // Clean the number from spaces, dashes, and existing plus signs
                string cleanNum = toPhoneNumber.Replace(" ", "").Replace("-", "").Replace("+", "").Replace("whatsapp:", "");
                
                // Force Malaysia country code (+60)
                if (cleanNum.StartsWith("0"))
                {
                    cleanNum = "60" + cleanNum.Substring(1);
                }
                else if (!cleanNum.StartsWith("60"))
                {
                    cleanNum = "60" + cleanNum;
                }

                string finalWhatsAppNumber = "whatsapp:+" + cleanNum;

                // Format FromNumber strictly
                string formattedFrom = _fromNumber.Trim();
                if (!formattedFrom.StartsWith("whatsapp:+"))
                {
                    string simpleFrom = formattedFrom.Replace("whatsapp:", "").Replace("+", "");
                    formattedFrom = "whatsapp:+" + simpleFrom;
                }

                var messageOptions = new CreateMessageOptions(new Twilio.Types.PhoneNumber(finalWhatsAppNumber))
                {
                    From = new Twilio.Types.PhoneNumber(formattedFrom),
                    Body = message
                };

                if (!string.IsNullOrEmpty(mediaUrl))
                {
                    messageOptions.MediaUrl = new List<Uri> { new Uri(mediaUrl) };
                }

                await MessageResource.CreateAsync(messageOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Twilio Exception Error: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
                }
                throw; 
            }
        }
    }
}
