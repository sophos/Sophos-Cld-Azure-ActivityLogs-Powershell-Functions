using System;
using System.IO;
using System.Collections.Generic;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;


namespace NwNsgProject
{
    public static class Stage1BlobTriggerActivity
    {
        const int MAXDOWNLOADBYTES = 1024000;

        [FunctionName("Stage1BlobTriggerActivity")]
        public static async Task Run(
            [BlobTrigger("%blobContainerNameActivity%/resourceId=/SUBSCRIPTIONS/{subId}/y={blobYear}/m={blobMonth}/d={blobDay}/h={blobHour}/m={blobMinute}/PT1H.json", Connection = "nsgSourceDataConnection")] AppendBlobClient myBlobActivity,
            [Queue("activitystage1", Connection = "AzureWebJobsStorage")] ICollector<Chunk> outputChunksActivity,
            string subId, string blobYear, string blobMonth, string blobDay, string blobHour, string blobMinute,
            ILogger log)
        {
            try
            {
                log.LogInformation("starting");
                string nsgSourceDataAccount = Util.GetEnvironmentVariable("nsgSourceDataAccount");
                if (nsgSourceDataAccount.Length == 0)
                {
                    log.LogError("Value for nsgSourceDataAccount is required.");
                    throw new System.ArgumentNullException("nsgSourceDataAccount", "Please provide setting.");
                }

                string blobContainerName = Util.GetEnvironmentVariable("blobContainerNameActivity");
                if (blobContainerName.Length == 0)
                {
                    log.LogError("Value for blobContainerName is required.");
                    throw new System.ArgumentNullException("blobContainerName", "Please provide setting.");
                }

                var blobDetails = new BlobDetailsActivity(subId, blobYear, blobMonth, blobDay, blobHour, blobMinute);

                string storageConnectionString = Util.GetEnvironmentVariable("AzureWebJobsStorage");
                // Create a TableClient instance
                TableClient tableClient = new TableClient(storageConnectionString, "activitycheckpoints");
                // Create table if not exist
                await tableClient.CreateIfNotExistsAsync();

                // get checkpoint
                Checkpoint checkpoint = await Checkpoint.GetCheckpointActivity(blobDetails, tableClient);
                // break up the block list into 10k chunks

                var blobProperties = await myBlobActivity.GetPropertiesAsync();
                long blobSize = blobProperties.Value.ContentLength;
                long chunklength = blobSize - checkpoint.StartingByteOffset;
                if(chunklength >10)
                {
                    Chunk newchunk = new Chunk
                        {
                            BlobName = blobContainerName + "/" + myBlobActivity.Name,
                            Length = chunklength,
                            LastBlockName = "",
                            Start = checkpoint.StartingByteOffset,
                            BlobAccountConnectionName = nsgSourceDataAccount
                        };

                    checkpoint.PutCheckpointActivity(tableClient, blobSize);
                    outputChunksActivity.Add(newchunk);
                }
            }
            catch (Exception e)
            {
                 log.LogError(e, "Function Stage1BlobTriggerActivity is failed to process request");
            }

        }
    }

}