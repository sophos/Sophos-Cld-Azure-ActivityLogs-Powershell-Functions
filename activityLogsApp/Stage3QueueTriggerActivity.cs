using Azure;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Extensions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace NwNsgProject
{
    public static class Stage3QueueTriggerActivity
    {
        [FunctionName("Stage3QueueTriggerActivity")]
        public static async Task Run(
            [QueueTrigger("activitystage2", Connection = "AzureWebJobsStorage")]Chunk inputChunk,
            IBinder binder, ILogger log)
        {
            try
            {
                string nsgSourceDataAccount = Util.GetEnvironmentVariable("nsgSourceDataAccount");
                if (nsgSourceDataAccount.Length == 0)
                {
                    log.LogError("Value for nsgSourceDataAccount is required.");
                    throw new ArgumentNullException("nsgSourceDataAccount", "Please supply in this setting the name of the connection string from which NSG logs should be read.");
                }

                var blobClient = await binder.BindAsync<BlobClient>(new BlobAttribute(inputChunk.BlobName)
                {
                    Connection = nsgSourceDataAccount
                });
                 var range = new HttpRange(inputChunk.Start, inputChunk.Length);
                var downloadOptions = new BlobDownloadOptions
                {
                    Range = range
                };
                BlobDownloadStreamingResult response = await blobClient.DownloadStreamingAsync(downloadOptions);
                string nsgMessagesString;
                using (var stream = response.Content)
                using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    nsgMessagesString = await reader.ReadToEndAsync();
                }

                // skip past the leading comma
                string trimmedMessages = nsgMessagesString.Trim();
                int curlyBrace = trimmedMessages.IndexOf('{');
                string newClientContent = "{\"records\":[";
                newClientContent += trimmedMessages.Substring(curlyBrace);
                newClientContent += "]}";
                newClientContent = newClientContent.Replace(System.Environment.NewLine, ",");
                await SendMessagesDownstream(newClientContent, log);
            }
            catch (Exception e)
            {
                log.LogError(e, "Function Stage3QueueTriggerActivity is failed to process request");
            }

        }

        public static async Task SendMessagesDownstream(string myMessages, ILogger log)
        {
            await obAvidSecure(myMessages, log);

        }

        static async Task obAvidSecure(string newClientContent, ILogger log)
        {

            string avidAddress = Util.GetEnvironmentVariable("avidActivityAddress");

            if (avidAddress.Length == 0)
            {
                log.LogError("Values for splunkAddress and splunkToken are required.");
                return;
            }
            string customerid = Util.GetEnvironmentVariable("customerId");
            ActivityLogsRecords logs = JsonConvert.DeserializeObject<ActivityLogsRecords>(newClientContent);
            logs.uuid = customerid;
            string jsonString = JsonConvert.SerializeObject(logs);

            var client = new SingleHttpClientInstance();
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, avidAddress);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                req.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await SingleHttpClientInstance.SendToSplunk(req);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new System.Net.Http.HttpRequestException($"StatusCode from Splunk: {response.StatusCode}, and reason: {response.ReasonPhrase}");
                }
            }
            catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
            }
            catch (Exception f)
            {
                throw new System.Exception("Sending to Splunk. Unplanned exception.", f);
            }

        }




        public class SingleHttpClientInstance
        {
            private static readonly HttpClient HttpClient;

            static SingleHttpClientInstance()
            {
                HttpClient = new HttpClient();
                HttpClient.Timeout = new TimeSpan(0, 1, 0);
            }

            public static async Task<HttpResponseMessage> SendToSplunk(HttpRequestMessage req)
            {
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }

        }
    }
}