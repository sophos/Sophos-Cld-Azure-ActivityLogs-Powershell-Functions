using Azure;
using System;
using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Extensions.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Text;
using Azure.Identity;

namespace NwNsgProject
{
    public static class Stage2QueueTriggerActivity
    {
        const int MAX_CHUNK_SIZE = 1024000;

        [FunctionName("Stage2QueueTriggerActivity")]
        public static async Task Run(
            [QueueTrigger("activitystage1")] Chunk inputChunk,
            [Queue("activitystage2")] ICollector<Chunk> outputQueue, ILogger log)
        {
            try
            {
                log.LogInformation($"C# Queue trigger function processed: {inputChunk}");

                if (inputChunk.Length < MAX_CHUNK_SIZE)
                {
                    outputQueue.Add(inputChunk);
                    return;
                }

                var credential = new DefaultAzureCredential();
               string subscriptionIds = Util.GetEnvironmentVariable("subscriptionIds");
                 if (string.IsNullOrEmpty(subscriptionIds))
                  {
                      log.LogError("Value for subscriptionIds is required.");
                      throw new ArgumentNullException("subscriptionIds", "SubscriptionId is not found in environment settings.");
                  }
                 string customerId = Util.GetEnvironmentVariable("customerId");
                 if (string.IsNullOrEmpty(customerId))
                  {
                      log.LogError("Value for customerId is required.");
                      throw new ArgumentNullException("customerId", "customerId is not found in environment settings..");
                  }
                  log.LogInformation("POC | value for subscriptionIds: {subscriptionIds}", subscriptionIds);

                  log.LogInformation("POC | value for customerId: {customerId}", customerId);

                 string storageAccountName = "lavidact" + subscriptionIds.Replace("-", "").Substring(0, 8) + customerId.Replace("-", "").Substring(0, 8);
                    log.LogInformation("POC | value for storageAccountName: {StorageAccountName}", storageAccountName);
                string blobAccountUrl = $"https://{storageAccountName}.blob.core.windows.net/";
                var blobServiceClient = new BlobServiceClient(new Uri(blobAccountUrl), credential);
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

                int startingByte = 0;
                var chunkCount = 0;
                var newChunk = GetNewChunk(inputChunk, chunkCount++, log, 0);
                int endingByte = FindNextRecordRecurse(nsgMessagesString, startingByte, 0, log);
                int length = endingByte - startingByte + 1;

                while (length != 0)
                {
                    if (newChunk.Length + length > MAX_CHUNK_SIZE)
                    {
                        outputQueue.Add(newChunk);

                        newChunk = GetNewChunk(inputChunk, chunkCount++, log, newChunk.Start + newChunk.Length);
                    }

                    newChunk.Length += length;
                    startingByte += length;

                    endingByte = FindNextRecordRecurse(nsgMessagesString, startingByte, 0, log);
                    length = endingByte - startingByte + 1;
                }

                if (newChunk.Length > 0)
                {
                    outputQueue.Add(newChunk);
                    //log.LogInformation($"Chunk starts at {newChunk.Start}, length is {newChunk.Length}");
                }
            }
            catch (Exception e)
            {
                log.LogError(e, "Function Stage2QueueTriggerActivity is failed to process request");
            }

        }

        public static Chunk GetNewChunk(Chunk thisChunk, int index, ILogger log, long start = 0)
        {
            var chunk = new Chunk
            {
                BlobName = thisChunk.BlobName,
                BlobAccountConnectionName = "ManagedIdentity",
                LastBlockName = string.Format("{0}-{1}", index, thisChunk.LastBlockName),
                Start = (start == 0 ? thisChunk.Start : start),
                Length = 0
            };

            //log.LogInformation($"new chunk: {chunk.ToString()}");

            return chunk;
        }

        public static long FindNextRecord(byte[] array, long startingByte)
        {
            var arraySize = array.Length;
            var endingByte = startingByte;
            var curlyBraceCount = 0;
            var insideARecord = false;

            for (long index = startingByte; index < arraySize; index++)
            {
                endingByte++;

                if (array[index] == '{')
                {
                    insideARecord = true;
                    curlyBraceCount++;
                }

                curlyBraceCount -= (array[index] == '}' ? 1 : 0);

                if (insideARecord && curlyBraceCount == 0)
                {
                    break;
                }
            }

            return endingByte - startingByte;
        }

        public static int FindNextRecordRecurse(string nsgMessages, int startingByte, int braceCounter, ILogger log)
        {
            if (startingByte == nsgMessages.Length)
            {
                return startingByte - 1;
            }

            int nextBrace = nsgMessages.IndexOfAny(new Char[] { '}', '{' }, startingByte);

            if (nsgMessages[nextBrace] == '{')
            {
                braceCounter++;
                nextBrace = FindNextRecordRecurse(nsgMessages, nextBrace + 1, braceCounter, log);
            }
            else
            {
                braceCounter--;
                if (braceCounter > 0)
                {
                    nextBrace = FindNextRecordRecurse(nsgMessages, nextBrace + 1, braceCounter, log);
                }
            }

            return nextBrace;
        }
    }


}