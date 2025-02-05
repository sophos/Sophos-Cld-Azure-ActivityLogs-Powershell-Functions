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

namespace NwNsgProject
{
    public static class Stage2QueueTriggerActivity
    {
        const int MAX_CHUNK_SIZE = 1024000;

        [FunctionName("Stage2QueueTriggerActivity")]
        public static async Task Run(
            [QueueTrigger("activitystage1", Connection = "AzureWebJobsStorage")]Chunk inputChunk,
            [Queue("activitystage2", Connection = "AzureWebJobsStorage")] ICollector<Chunk> outputQueue,
            IBinder binder, ILogger log)
        {
            try
            {
                log.LogInformation($"C# Queue trigger function processed: {inputChunk}");

                if (inputChunk.Length < MAX_CHUNK_SIZE)
                {
                    outputQueue.Add(inputChunk);
                    return;
                }

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
                BlobAccountConnectionName = thisChunk.BlobAccountConnectionName,
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