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
using Azure.Identity;

namespace NwNsgProject
{
    public static class Stage1BlobTriggerActivity
    {
        const int MAXDOWNLOADBYTES = 1024000;

        [FunctionName("Stage1BlobTriggerActivity")]
        public static async Task Run(
            [BlobTrigger("%blobContainerNameActivity%/resourceId=/SUBSCRIPTIONS/{subId}/y={blobYear}/m={blobMonth}/d={blobDay}/h={blobHour}/m={blobMinute}/PT1H.json")] AppendBlobClient myBlobActivity,
            [Queue("activitystage1")] ICollector<Chunk> outputChunksActivity,
            string subId, string blobYear, string blobMonth, string blobDay, string blobHour, string blobMinute,
            ILogger log)
        {
            try
            {
                log.LogInformation("starting");

                string blobContainerName = Util.GetEnvironmentVariable("blobContainerNameActivity");
                if (blobContainerName.Length == 0)
                {
                    log.LogError("Value for blobContainerName is required.");
                    throw new System.ArgumentNullException("blobContainerName", "Please provide setting.");
                }

                var blobDetails = new BlobDetailsActivity(subId, blobYear, blobMonth, blobDay, blobHour, blobMinute);

                // Authenticate using Managed Identity
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

                string tableEndpoint = $"https://{storageAccountName}.table.core.windows.net/";
                TableClient tableClient = new TableClient(new Uri(tableEndpoint), "activitycheckpoints", credential);
                // Create table if not exist
                await tableClient.CreateIfNotExistsAsync();

                // get checkpoint
                Checkpoint checkpoint = await Checkpoint.GetCheckpointActivity(blobDetails, tableClient);
                // break up the block list into 10k chunks

                var blobProperties = await myBlobActivity.GetPropertiesAsync();
                long blobSize = blobProperties.Value.ContentLength;
                long chunklength = blobSize - checkpoint.StartingByteOffset;
                if (chunklength > 10)
                {
                    Chunk newchunk = new Chunk
                    {
                        BlobName = blobContainerName + "/" + myBlobActivity.Name,
                        Length = chunklength,
                        LastBlockName = "",
                        Start = checkpoint.StartingByteOffset,
                        BlobAccountConnectionName = "ManagedIdentity"
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
