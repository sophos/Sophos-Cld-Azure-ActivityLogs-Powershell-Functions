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
using Azure.Identity;

namespace NwNsgProject
{
    public static class Stage3QueueTriggerActivity
    {
        [FunctionName("Stage3QueueTriggerActivity")]
        public static async Task Run(
            [QueueTrigger("activitystage2")] Chunk inputChunk,ILogger log)
        {
            try
            {
                var credential = new DefaultAzureCredential();

                // Get the storage account name from environment variables
                string storageAccountName = Util.GetEnvironmentVariable("storageAccountName");
                if (string.IsNullOrEmpty(storageAccountName))
                {
                    log.LogError("Value for storageAccountName is required.");
                    throw new ArgumentNullException("storageAccountName", "Please supply the storage account name in environment settings.");
                }

                // Build the Blob Service Client using Managed Identity
                string blobAccountUrl = $"https://{storageAccountName}.blob.core.windows.net/";
                var blobServiceClient = new BlobServiceClient(new Uri(blobAccountUrl), credential);

                // Get a BlobClient for the specific blob
                var blobClient = blobServiceClient.GetBlobClient(inputChunk.BlobName);

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
