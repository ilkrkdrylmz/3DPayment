using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using ThreeDPayment.Requests;
using ThreeDPayment.Results;

namespace ThreeDPayment.Providers
{
    public class NestPayPaymentProvider : IPaymentProvider
    {
        private readonly HttpClient _client;

        public NestPayPaymentProvider(IHttpClientFactory httpClientFactory)
        {
            _client = httpClientFactory.CreateClient();
        }

        public Task<PaymentGatewayResult> ThreeDGatewayRequest(PaymentGatewayRequest request)
        {
            try
            {
                string clientId = request.BankParameters["clientId"];
                string processType = request.BankParameters["processType"];
                string storeKey = request.BankParameters["storeKey"];
                string storeType = request.BankParameters["storeType"];
                string random = DateTime.Now.ToString();

                string installment = request.Installment > 1 ? request.Installment.ToString() : "";

                //Payten güncel dökümantasyonuna göre hazırlanmıştır. Parametre sıralaması hash oluşturmadan dolayı önemlidir.
                Dictionary<string, object> parameters = new Dictionary<string, object>()
                {
                    { "pan", request.CardNumber },
                    { "cv2", request.CvvCode },
                    { "Ecom_Payment_Card_ExpDate_Year", request.ExpireYear.ToString() },
                    { "Ecom_Payment_Card_ExpDate_Month", request.ExpireMonth.ToString("00") },
                    { "clientid", clientId },
                    { "amount", request.TotalAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")).Replace(".", "").Replace(",", ".") },
                    { "oid", request.OrderNumber },
                    { "okUrl", request.CallbackUrl.ToString() },
                    { "failUrl", request.CallbackUrl.ToString() },
                    { "rnd", random},
                    { "storetype", storeType },
                    { "lang", "tr" },
                    { "currency", request.CurrencyIsoCode },
                    { "installment", installment },
                    { "taksit", installment },
                    { "islemtipi", "Auth" },
                    { "hashAlgorithm", "ver3" }
                };

                string hash = string.Join("|", parameters.OrderBy(s => s.Key).Select(s => s.Value.ToString().Replace("|", "\\|").Replace("\\", "\\\\"))) + "|" + storeKey;
                hash = GetSHA512(hash);
                parameters.Add("hash", hash);

                return Task.FromResult(PaymentGatewayResult.Successed(parameters, request.BankParameters["gatewayUrl"]));
            }
            catch (Exception ex)
            {
                return Task.FromResult(PaymentGatewayResult.Failed(ex.ToString()));
            }
        }

        public async Task<VerifyGatewayResult> VerifyGateway(VerifyGatewayRequest request, PaymentGatewayRequest gatewayRequest, IFormCollection form)
        {
            if (form == null)
            {
                return VerifyGatewayResult.Failed("Form verisi alınamadı.");
            }

            var mdStatus = form["mdStatus"].ToString();
            if (string.IsNullOrEmpty(mdStatus))
            {
                return VerifyGatewayResult.Failed(form["mdErrorMsg"], form["ProcReturnCode"]);
            }

            var response = form["Response"].ToString();
            //mdstatus 1,2,3 veya 4 olursa 3D doğrulama geçildi anlamına geliyor
            if (!MdStatusCodes.Contains(mdStatus))
            {
                return VerifyGatewayResult.Failed($"{response} - {form["mdErrorMsg"]}", form["ProcReturnCode"]);
            }


            string clientId = request.BankParameters["clientId"];
            string userName = request.BankParameters["userName"];
            string password = request.BankParameters["password"];
            string verifyUrl = request.BankParameters["verifyUrl"];
            string storeType = request.BankParameters["storeType"];
            string totalAmount = gatewayRequest.TotalAmount.ToString(new CultureInfo("en-US"));
            string Number = form["md"].FirstOrDefault();
            string PayerTxnId = form["xid"].FirstOrDefault();
            string PayerSecurityLevel = form["eci"].FirstOrDefault();
            string PayerAuthenticationCode = form["cavv"].FirstOrDefault();
            string oid = form["oid"].FirstOrDefault();
            string taksit = form["taksit"].FirstOrDefault() ?? string.Empty;
            string transactionId = string.Empty;
            string ProcReturnCode = string.Empty;
            string bankResponse = string.Empty;
            string bankRefNo = string.Empty;

            if (!storeType.Equals("3D_PAY_HOSTING"))
            {
                string requestXml = $@"DATA=<?xml version=""1.0"" encoding=""ISO-8859-9""?>
                                    <CC5Request>
                                      <Name>{userName}</Name>
                                      <Password>{password}</Password>
                                      <ClientId>{clientId}</ClientId>
                                      <OrderId>{oid}</OrderId>
                                      <IPAddress>{(string.IsNullOrEmpty(request.CustomerIpAddress) ? "127.0.0.1" : request.CustomerIpAddress)}</IPAddress>
                                      <Type>Auth</Type>
                                      <Amount>{totalAmount}</Amount>
                                      <Currency>{gatewayRequest.CurrencyIsoCode}</Currency>
                                      <Number>{Number}</Number>
                                      <PayerTxnId>{PayerTxnId}</PayerTxnId>
                                      <PayerSecurityLevel>{PayerSecurityLevel}</PayerSecurityLevel>
                                      <PayerAuthenticationCode>{PayerAuthenticationCode}</PayerAuthenticationCode>";

                if (!string.IsNullOrEmpty(taksit) && taksit != "0" && taksit != "1")
                    requestXml += $"<Taksit>{taksit}</Taksit>";
                requestXml += "</CC5Request>";
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                var apiResponse = await _client.PostAsync(verifyUrl, new StringContent(requestXml, Encoding.UTF8, "application/x-www-form-urlencoded"));
                string responseContent = await apiResponse.Content.ReadAsStringAsync();
                var xmlDocument = new XmlDocument();
                xmlDocument.LoadXml(responseContent);

                if (xmlDocument.SelectSingleNode("CC5Response/Response") == null || xmlDocument.SelectSingleNode("CC5Response/Response").InnerText != "Approved")
                {
                    var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
                    var errorCode = xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode").InnerText ?? string.Empty;
                    if (string.IsNullOrEmpty(errorMessage))
                        errorMessage = "Bankadan hata mesajı alınamadı.";
                    if (string.IsNullOrEmpty(errorCode))
                        errorCode = "Bankadan hata kodu alınamadı";

                    return VerifyGatewayResult.Failed(errorMessage, errorCode);
                }

                if (xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode") == null || xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode").InnerText != "00")
                {
                    var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
                    var errorCode = xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode").InnerText ?? string.Empty;
                    if (string.IsNullOrEmpty(errorMessage))
                        errorMessage = "Bankadan hata mesajı alınamadı.";
                    if (string.IsNullOrEmpty(errorCode))
                        errorCode = "Bankadan hata kodu alınamadı";

                    return VerifyGatewayResult.Failed(errorMessage, errorCode);
                }

                transactionId = xmlDocument.SelectSingleNode("CC5Response/TransId")?.InnerText ?? string.Empty;
                ProcReturnCode = xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode")?.InnerText ?? string.Empty;
                bankResponse = xmlDocument.SelectSingleNode("CC5Response/Extra/HOSTMSG")?.InnerText ?? string.Empty;
            }
            else
            {
                if (response != "Approved" || form["mdErrorMsg"] != "Success")
                {
                    return VerifyGatewayResult.Failed($"{response} - {form["mdErrorMsg"]}", form["ProcReturnCode"]);
                }

                transactionId = form["TransId"];
                ProcReturnCode = form["ProcReturnCode"];
                bankResponse = form["mdErrorMsg"];
                bankRefNo = $"{form["HostRefNum"]}-{form["AuthCode"]}";
            }

            // var hashBuilder = new StringBuilder();
            // hashBuilder.Append(request.BankParameters["clientId"]);
            // hashBuilder.Append(form["oid"].FirstOrDefault());
            // hashBuilder.Append(form["AuthCode"].FirstOrDefault());
            // hashBuilder.Append(form["ProcReturnCode"].FirstOrDefault());
            // hashBuilder.Append(form["Response"].FirstOrDefault());
            // hashBuilder.Append(form["mdStatus"].FirstOrDefault());
            // hashBuilder.Append(form["cavv"].FirstOrDefault());
            // hashBuilder.Append(form["eci"].FirstOrDefault());
            // hashBuilder.Append(form["md"].FirstOrDefault());
            // hashBuilder.Append(form["rnd"].FirstOrDefault());
            // hashBuilder.Append(request.BankParameters["storeKey"]);

            // var hashData = GetSha1(hashBuilder.ToString());
            // if (!form["HASH"].Equals(hashData))
            // {
            //     return VerifyGatewayResult.Failed("Güvenlik imza doğrulaması geçersiz.");
            // }

            int installment = gatewayRequest.Installment;
            int extraInstallment = 0;
            int.TryParse(form["taksit"], out installment);
            int.TryParse(form["EXTRA.HOSTMSG"], out extraInstallment);
            int.TryParse(form["EXTRA.ARTITAKSIT"], out extraInstallment);

            if (storeType.Equals("3D_PAY_HOSTING"))
                extraInstallment = extraInstallment - installment;

            if (string.IsNullOrEmpty(bankRefNo))
                bankRefNo = transactionId;

            return VerifyGatewayResult.Successed(transactionId, bankRefNo, installment, extraInstallment, $"{response} - {bankResponse}", ProcReturnCode);
        }

        public async Task<CancelPaymentResult> CancelRequest(CancelPaymentRequest request)
        {
            string clientId = request.BankParameters["clientId"];
            string userName = request.BankParameters["cancelUsername"];
            string password = request.BankParameters["cancelUserPassword"];

            string requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                                    <CC5Request>
                                      <Name>{userName}</Name>
                                      <Password>{password}</Password>
                                      <ClientId>{clientId}</ClientId>
                                      <Type>Void</Type>
                                      <OrderId>{request.OrderNumber}</OrderId>
                                    </CC5Request>";

            var response = await _client.PostAsync(request.BankParameters["verifyUrl"], new StringContent(requestXml, Encoding.UTF8, "text/xml"));
            string responseContent = await response.Content.ReadAsStringAsync();

            var xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(responseContent);

            if (xmlDocument.SelectSingleNode("CC5Response/Response") == null ||
                xmlDocument.SelectSingleNode("CC5Response/Response").InnerText != "Approved")
            {
                var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
                if (string.IsNullOrEmpty(errorMessage))
                    errorMessage = "Bankadan hata mesajı alınamadı.";

                return CancelPaymentResult.Failed(errorMessage);
            }

            if (xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode") == null ||
                xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode").InnerText != "00")
            {
                var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
                if (string.IsNullOrEmpty(errorMessage))
                    errorMessage = "Bankadan hata mesajı alınamadı.";

                return CancelPaymentResult.Failed(errorMessage);
            }

            var transactionId = xmlDocument.SelectSingleNode("CC5Response/TransId")?.InnerText ?? string.Empty;
            return CancelPaymentResult.Successed(transactionId, transactionId);
        }

        public async Task<RefundPaymentResult> RefundRequest(RefundPaymentRequest request)
        {
            string clientId = request.BankParameters["clientId"];
            string userName = request.BankParameters["refundUsername"];
            string password = request.BankParameters["refundUserPassword"];

            string requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                                    <CC5Request>
                                      <Name>{userName}</Name>
                                      <Password>{password}</Password>
                                      <ClientId>{clientId}</ClientId>
                                      <Type>Credit</Type>
                                      <OrderId>{request.OrderNumber}</OrderId>
                                    </CC5Request>";

            var response = await _client.PostAsync(request.BankParameters["verifyUrl"], new StringContent(requestXml, Encoding.UTF8, "text/xml"));
            string responseContent = await response.Content.ReadAsStringAsync();

            var xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(responseContent);

            if (xmlDocument.SelectSingleNode("CC5Response/Response") == null ||
                xmlDocument.SelectSingleNode("CC5Response/Response").InnerText != "Approved")
            {
                var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
                if (string.IsNullOrEmpty(errorMessage))
                    errorMessage = "Bankadan hata mesajı alınamadı.";

                return RefundPaymentResult.Failed(errorMessage);
            }

            if (xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode") == null ||
                xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode").InnerText != "00")
            {
                var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
                if (string.IsNullOrEmpty(errorMessage))
                    errorMessage = "Bankadan hata mesajı alınamadı.";

                return RefundPaymentResult.Failed(errorMessage);
            }

            var transactionId = xmlDocument.SelectSingleNode("CC5Response/TransId")?.InnerText ?? string.Empty;
            return RefundPaymentResult.Successed(transactionId, transactionId);
        }

        public async Task<PaymentDetailResult> PaymentDetailRequest(PaymentDetailRequest request)
        {
            string clientId = request.BankParameters["clientId"];
            string userName = request.BankParameters["userName"];
            string password = request.BankParameters["password"];

            string requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                                    <CC5Request>
                                        <Name>{userName}</Name>
                                        <Password>{password}</Password>
                                        <ClientId>{clientId}</ClientId>
                                        <OrderId>{request.OrderNumber}</OrderId>
                                        <Extra>
                                            <ORDERDETAIL>QUERY</ORDERDETAIL>
                                        </Extra>
                                    </CC5Request>";

            var response = await _client.PostAsync(request.BankParameters["verifyUrl"], new StringContent(requestXml, Encoding.UTF8, "text/xml"));
            string responseContent = await response.Content.ReadAsStringAsync();

            var xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(responseContent);

            string finalStatus = xmlDocument.SelectSingleNode("CC5Response/Extra/ORDER_FINAL_STATUS")?.InnerText ?? string.Empty;
            string transactionId = xmlDocument.SelectSingleNode("CC5Response/Extra/TRX_1_TRAN_UID")?.InnerText;
            string referenceNumber = xmlDocument.SelectSingleNode("CC5Response/Extra/TRX_1_TRAN_UID")?.InnerText;
            string cardPrefix = xmlDocument.SelectSingleNode("CC5Response/Extra/TRX_1_CARDBIN")?.InnerText;
            int.TryParse(cardPrefix, out int cardPrefixValue);

            string installment = xmlDocument.SelectSingleNode("CC5Response/Extra/TRX_1_INSTALMENT")?.InnerText ?? "0";
            string bankMessage = xmlDocument.SelectSingleNode("CC5Response/Response")?.InnerText;
            string responseCode = xmlDocument.SelectSingleNode("CC5Response/ProcReturnCode")?.InnerText;

            if (finalStatus.Equals("SALE", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(installment, out int installmentValue);
                return PaymentDetailResult.PaidResult(transactionId, referenceNumber, cardPrefixValue.ToString(), installmentValue, 0, bankMessage, responseCode);
            }
            else if (finalStatus.Equals("VOID", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentDetailResult.CanceledResult(transactionId, referenceNumber, bankMessage, responseCode);
            }
            else if (finalStatus.Equals("REFUND", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentDetailResult.RefundedResult(transactionId, referenceNumber, bankMessage, responseCode);
            }

            var errorMessage = xmlDocument.SelectSingleNode("CC5Response/ErrMsg")?.InnerText ?? string.Empty;
            if (string.IsNullOrEmpty(errorMessage))
                errorMessage = "Bankadan hata mesajı alınamadı.";

            return PaymentDetailResult.FailedResult(errorMessage: errorMessage);
        }

        public Dictionary<string, string> TestParameters => new Dictionary<string, string>
        {
            { "clientId", "" },
            { "processType", "Auth" },
            { "storeKey", "" },
            { "storeType", "3D_PAY" },
            { "gatewayUrl", "https://entegrasyon.asseco-see.com.tr/fim/est3Dgate" },
            { "userName", "" },
            { "password", "" },
            { "verifyUrl", "https://entegrasyon.asseco-see.com.tr/fim/api" }
        };

        private static string GetSha1(string text)
        {
            var cryptoServiceProvider = new SHA1CryptoServiceProvider();
            var inputBytes = cryptoServiceProvider.ComputeHash(Encoding.UTF8.GetBytes(text));
            var hashData = Convert.ToBase64String(inputBytes);

            return hashData;
        }

        private string GetSHA512(string text)
        {
            var cryptoServiceProvider = new SHA512CryptoServiceProvider();
            var inputbytes = cryptoServiceProvider.ComputeHash(Encoding.UTF8.GetBytes(text));
            var hashData = Convert.ToBase64String(inputbytes);

            return hashData;
        }

        private static readonly string[] MdStatusCodes = { "1", "2", "3", "4" };
    }
}
