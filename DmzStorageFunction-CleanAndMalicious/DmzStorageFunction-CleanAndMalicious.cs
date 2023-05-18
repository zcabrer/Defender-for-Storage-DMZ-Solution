// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace DmzStorageFunction
{
    public static class DmzStorageFunction_CleanAndMalicious
    {
        private const string CleanStorageAccount = "securestoragesc";
        private const string QuarantineStorageAccount = "quarantinestoragesc";
        private const string AntimalwareScanEventType = "Microsoft.Security.MalwareScanningResult";
        private const string MaliciousVerdict = "Malicious";
        private const string CleanVerdict = "No threats found";

        [FunctionName("MoveBlobEventTrigger")]
        public static async Task RunAsync([EventGridTrigger] EventGridEvent eventGridEvent, ILogger log)
        {
            if (eventGridEvent.EventType != AntimalwareScanEventType)
            {
                log.LogInformation("Event type is not an {0} event, event type:{1}", AntimalwareScanEventType, eventGridEvent.EventType);
                return;
            }

            var storageAccountName = eventGridEvent?.Subject?.Split("/")[^1];
            log.LogInformation("Received new scan result for storage {0}", storageAccountName);
            var decodedEventData = JsonDocument.Parse(eventGridEvent.Data).RootElement.ToString();
            var eventData = JsonDocument.Parse(decodedEventData).RootElement;
            var verdict = eventData.GetProperty("scanResultType").GetString();
            var blobUriString = eventData.GetProperty("blobUri").GetString();

            if (verdict == null || blobUriString == null)
            {
                log.LogError("Event data doesn't contain 'verdict' or 'blobUri' fields");
                throw new ArgumentException("Event data doesn't contain 'verdict' or 'blobUri' fields");
            }

            if (verdict == MaliciousVerdict)
            {
                bool isClean = false;
                var blobUri = new Uri(blobUriString);
                log.LogInformation("blob {0} is malicious, moving it to Storage Account {1}", blobUri, QuarantineStorageAccount);
                try
                {
                    await MoveBlobAsync(blobUri, log, isClean);
                }
                catch (Exception e)
                {
                    log.LogError(e, "Can't move blob to Storage Account '{0}'", QuarantineStorageAccount);
                    throw;
                }
            }
            if (verdict == CleanVerdict)
            {
                bool isClean = true;
                var blobUri = new Uri(blobUriString);
                log.LogInformation("blob {0} is clean, moving it to Storage Account {1}", blobUri, CleanStorageAccount);
                try
                {
                    await MoveBlobAsync(blobUri, log, isClean);
                }
                catch (Exception e)
                {
                    log.LogError(e, "Can't move blob to Storage Account '{0}'", CleanStorageAccount);
                    throw;
                }
            }
        }

        private static async Task MoveBlobAsync(Uri blobUri, ILogger log, bool isClean)
        {
            var destStorageAccountName = isClean ? CleanStorageAccount : QuarantineStorageAccount;
            
            DefaultAzureCredential defaultAzureCredential = new DefaultAzureCredential();

            BlobUriBuilder srcBlobUri = new BlobUriBuilder(blobUri);
            BlobUriBuilder destBlobUri = new BlobUriBuilder(new Uri($"https://{destStorageAccountName}.blob.core.windows.net/{srcBlobUri.BlobContainerName}/{srcBlobUri.BlobName}"));

            BlobClient srcBlob = new BlobClient(blobUri, defaultAzureCredential);
            BlobClient destBlob = new BlobClient(destBlobUri.ToUri(), defaultAzureCredential);

            BlobContainerClient destContainer = new BlobContainerClient(destBlob.GetParentBlobContainerClient().Uri, defaultAzureCredential);

            log.LogInformation("Creating {0} container if it doesn't exist", destBlobUri.BlobContainerName);
            await destContainer.CreateIfNotExistsAsync();

            if (!await srcBlob.ExistsAsync())
            {
                log.LogError("blob {0} doesn't exist", blobUri);
                return;
            }

            log.LogInformation("MoveBlob: Copying blob to {0}", destBlob.Uri);

            // Lease the source blob to prevent changes during the copy operation
            BlobLeaseClient sourceBlobLease = new(srcBlob);

            // Create a Uri object with a SAS token appended - specify Read (r) permissions
            Uri sourceBlobSASURI = await GenerateUserDelegationSAS(srcBlob);

            try
            {
                await sourceBlobLease.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration);

                // Start the copy operation and wait for it to complete
                CopyFromUriOperation copyOperation = await destBlob.StartCopyFromUriAsync(sourceBlobSASURI);
                await copyOperation.WaitForCompletionAsync();
            }
            catch (Azure.RequestFailedException ex)
            {
                // Handle the exception
                log.LogInformation("Failed to move blob. Exception: {0}", ex);
            }
            finally
            {
                // Release the lease once the copy operation completes - Delete the source blob
                await sourceBlobLease.ReleaseAsync();
                log.LogInformation("MoveBlob: Deleting source blob {0}", srcBlob.Uri);
                await srcBlob.DeleteAsync();
                log.LogInformation("MoveBlob: blob moved successfully");
            }
        }

        async static Task<Uri> GenerateUserDelegationSAS(BlobClient sourceBlob)
        {
            BlobServiceClient blobServiceClient =
                sourceBlob.GetParentBlobContainerClient().GetParentBlobServiceClient();

            // Get a user delegation key for the Blob service that's valid for 1 day
            UserDelegationKey userDelegationKey =
                await blobServiceClient.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow,
                                                                  DateTimeOffset.UtcNow.AddHours(1));

            // Create a SAS token that's also valid for 1 day
            BlobSasBuilder sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = sourceBlob.BlobContainerName,
                BlobName = sourceBlob.Name,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow,
                ExpiresOn = DateTimeOffset.UtcNow.AddDays(1)
            };

            // Specify read permissions for the SAS
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            // Add the SAS token to the blob URI
            BlobUriBuilder blobUriBuilder = new BlobUriBuilder(sourceBlob.Uri)
            {
                // Specify the user delegation key
                Sas = sasBuilder.ToSasQueryParameters(userDelegationKey,
                                                      blobServiceClient.AccountName)
            };

            return blobUriBuilder.ToUri();
        }
    }
}
