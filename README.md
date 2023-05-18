# DMZ Storage Account with Defender for Storage

Defender for Storage's Malware Scanning feature secures storage accounts by scanning uploaded blobs in near real time to identity malicious content. This solution leverages Defender for Storage Malware Scanning, Event Grid Custom Topics, and Azure Functions, to automate data movement from a DMZ Storage Account to Clean and Quarantine Storage Accounts.

# Overview

![SolutionDiagram.png](images/SolutionDiagram.png)

A publicly accessible DMZ Storage Account is the initial landing zone, or target, for blob uploads. Defender for Storage is enabled on the DMZ Storage Account allowing Malware Scanning to scan all uploaded or modified blobs. When the scan is complete, Defender writes the scan results to a blob index tag on each individual blob. The result can either be `Malicious` or `No threats found`.

Defender for Storage settings are used to stream the scan results to a custom Event Grid Topic. A Function App subscribes to the Event Grid, and receives the scan results which triggers the Azure Function. The Function reads the scan result and moves the blob to the appropriate Storage Account (Clean or Quarantine) that is secured behind Private Endpoints.

# Deployment

## Table of contents

- [Deploy Resources](#deploy-resources)
  - [Storage Accounts](#storage-accounts)
  - [Event Grid Custom Topic](#event-grid-custom-topic)
  - [Function App](#function-app)
- [Permissions](#permissions)
- [Enable and Configure Defender for Storage](#enable-and-configure-defender-for-storage)
  - [Subscription Level](#subscription-level)
  - [Resource Level](#resource-level)
  - [Configure scan result event streaming](#configure-scan-result-event-streaming)
- [Deploy Code to Function App](#deploy-code-to-function-app)
- [Configure Event Subscription](#configure-event-subscription)
- [Validation](#validation)

## Deploy Resources

### Storage Accounts

This solution uses three storage accounts:

- DMZ Storage Account - Initial landing zone for blobs. Publicly accessible.
- Clean Storage Account - Blobs that are found to not have malware are moved here.
- Quarantine Storage Account - Blobs that are found to be malicious are moved here.

[How to Create a Storage Account](https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create?tabs=azure-portal)

For enhanced security, limit public access to the Clean and Quarantine Storage Accounts using [Storage Account Network Settings](https://learn.microsoft.com/en-us/azure/storage/common/storage-network-security?tabs=azure-portal). Ideally, the Clean storage account will not be publicly accessible but instead leverage [Private Endpoint](https://learn.microsoft.com/en-us/azure/storage/common/storage-private-endpoints) to limit the exposure of scanned clean blobs. Using Private Endpoints with Storage impacts the Function App connectivity. See [Function App Deployment](#function-app) for more details.

Notes:

- Keep in mind the Defender for Storage [limitations for storage accounts](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-storage-malware-scan#limitations).
- Deploy all Storage Accounts to the same region to improve copy performance.

### Event Grid Custom Topic

When Defender for Storage is enabled on a Storage Account, an Event Grid System Topic is automatically created to facilitate the Malware Scanning process. Removing this Event Grid System Topic will break Malware Scanning functionality. This System Topic also cannot be used for the purpose of this solution, as the `Microsoft.Security.MalwareScanningResult` Event Type is not available as a System Topic. Therefore, an Event Grid Custom Topic is required.

Follow this [how-to deployment guide](https://learn.microsoft.com/en-us/azure/event-grid/custom-event-quickstart-portal#create-a-custom-topic)

- Only create the Event Grid at this point, no need to create a message endpoint or subscription yet.
- Deploy to the same Region as the Storage Accounts

### Function App

Create a Function App in the same Resource Group and Region as the DMZ Storage Account using the **.NET Runtime Stack version 6** on **Windows**. Select a **Functions Premium** hosting plan if you need advanced networking cnofigurations like VNet Integration or Private Endpoint ([see more in the next section](#function-app-network-configurations)). The Function App requires a Storage Account to operate. Select a Storage Account other than the DMZ Storage Account. 

![functionAppcreate1](/images/functionapp-create-1.png)

Resources:

- [Develop Azure Functions by using Visual Studio Code](https://learn.microsoft.com/en-us/azure/azure-functions/functions-develop-vs-code?tabs=csharp)
- [Use a function as an event handler for Event Grid events](https://learn.microsoft.com/en-us/azure/event-grid/handler-functions)

#### Function App Network Configurations
If public access to the Storage Account is blocked with the Storage Account Firewall, additional steps need to be taken to allow the Function App to access the Storage Account. There are a few options:

1. If using Private Endpoints with the Storage Account, [Enable Virtual Network Integration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-networking-options?tabs=azure-cli#virtual-network-integration) on the Function App. After integrating the Function App into the VNet where the Private Endpoints are deployed, it will leverage the VNet's DNS settings and linked Private DNS Zones to resolve the Storage Accounts to their Private Endpoints. This requires a Premium App Service Plan. 
3. If using VNet whitelisting on the Storage Account Firewall, [Enable Virtual Network Integration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-networking-options?tabs=azure-cli#virtual-network-integration) on the Function App. Add the Function Apps VNet/Subnet to the Storage Account VNet whitelist.
2. If using IP whitelisting on the Storage Account Firewall, [locate the Function App public IPs](https://learn.microsoft.com/en-us/azure/azure-functions/ip-addresses?tabs=portal#find-outbound-ip-addresses) and add them to the whitelist.

Example Scenario: A destination Storage Account for clean blobs will deny all public access and have a Private Endpoint deployed to a VNet called **DMZ-VNet**. The Storage Account is only accessible via the Private Endpoint. For the Function App to access the Private Endpoint, it needs to be injected into the DMZ-VNet. This can be configured at Function App creation time like the screenshot below. It can also be configured in the **Network** settings of the Function App after deployment.

![functionapp-create-2.png](/images/functionapp-create-2.png)

## Permissions

The Function App needs permissions on all Storage Accounts to carry out the blob move operations. The `Storage Blob Data Contributor` built-in role has all the necessary permissions.

Its easiest to assign permissions using a System Assigned Managed Identity on the Function App.

1. On the Function App, navigate to **Identity** in the side menu. Set status to **On**, then **Save**.
2. Select **Add Role Assignment** and add the `Storage Blob Data Contributor` for each storage account. Alternatively, add the role at the Resource Group or Subscription level.

![functions permissions](images/functionspermissions.png)

## Enable and Configure Defender for Storage

Defender for Storage is enabled from either of two locations:

- Subscription level using the Defender for Cloud interface
- Resource level at each individual Storage Account

This solution only requires Defender for Storage to be enabled on the DMZ Storage Account where the blobs will be scanned. Clean and Quarantine Storage Accounts will only receive blobs that have been scanned in DMZ, so Defender for Storage Malware Scanning is not required there. The resource level enablement process was followed for this solution guide, however, both options are shown for completeness.

Ensure the Microsoft.EventGrid resource provider is registered on the Storage Account subscription. Use this PowerShell command to register the provider.

```PowerShell
Set-AzContext -Subscription "xxxx-xxxx-xxxx-xxxx"
Register-AzResourceProvider -ProviderNamespace Microsoft.EventGrid
```

### Subscription level

To enable Defender for Storage on all Storage Accounts within a subscription, navigate to **Defender for Cloud** -> **Environment Settings** -> **select the subscription**. Toggle **Storage** to **On** and click **Save**. In the Storage settings, you can optionally set a max limit on quantity of data scanned to put a cap on scanning costs.

After saving, it takes some time to onboard all Storage Accounts to Defender for Storage. To check the progress, navigate to each **Storage Account resource** -> **Microsoft Defender for Cloud** to see a status of either **Provisioning** or **On**.

[Additional Resources](https://learn.microsoft.com/en-us/azure/storage/common/azure-defender-storage-configure?toc=%2Fazure%2Fdefender-for-cloud%2Ftoc.json&tabs=enable-subscription)

![Enable using Defender for Cloud](images/enable-defender-subscritionlevel-1.png)

### Resource level

To enable Defender for Storage on a specific Storage Account, navigate to the **Storage Account** and select **Microsoft Defender for Cloud** in the side bar. Ensure **On-upload malware scanning** is checked, then select **Enable on storage account**. It takes some time for the enablement to complete.

![Enable using Storage](images/enable-defender-resourcelevel.png)

### Configure scan result event streaming

When Defender for Storage enablement is complete, navigate to the **Storage Account** and select **Microsoft Defender for Cloud** in the side bar. Select **Settings**, toggle **Override Defender for Storage subscription-level settings** to **On**. Check **Send scan results to Event-Grid topic** and select your Event Grid Custom Topic from the dropdown.

![SolutionDiagram.png](images/configure-storage-scan-result-streaming.png)

## Deploy Code to Function App

In this tutorial we will use one of two sample Function App projects. We download the sample project files, edit the storage account names to the ones deployed at the beginning of this guide, upload the edited project to our own repo, then use the Azure Portal to point our Function App to our own repo that contains the edited project. Having a github account is a prerequisite for these steps.

[1. Clone the sample Function App Repo](#1-clone-the-sample-function-app-repo)<br />
[2. Update the Storage Account Name](#2-update-the-storage-account-name)<br />
[3. Upload edited code to your repo](#3-upload-edited-code-to-your-repo)<br />
[4. Deploy to the Function App using the Azure Portal](#4-deploy-code-to-the-function-app-using-the-azure-portal)

### 1. Clone the sample Function App Repo

This project includes two Function App code samples:

- [DmzStorageFunction-CleanOnly](https://github.com/zcabrer/Defender-for-Storage-DMZ-Solution/tree/master/DmzStorageFunction-CleanOnly): Moves scanned blobs to a destination storage account that are marked as clean. Blobs marked as malicious remain in the DMZ Storage Account. Requires two Storage Accounts - DMZ Storage Account and Clean Storage Account.
- [DmzStorageFunction-CleanAndMalicious](https://github.com/zcabrer/Defender-for-Storage-DMZ-Solution/tree/master/DmzStorageFunction-CleanAndMalicious): Moves scanned blobs to one of two destination storage accounts depending on the scan results. Requires three Storage Accounts total - DMZ Storage Account, Clean Storage Account, and Malicious Storage Account.

Download a .zip of this repo by going to **Code** -> **Download ZIP**. Extract the files to a folder on your local system. Choose which of the two Function App samples to use and open that folder. 

![deployfunction-1](/images/deployfunction-1.png)

### 2. Update the Storage Account Name

Open the folder with the extracted Function App sample (either ```DmzStorageFunction-CleanOnly``` or ```DmzStorageFunction-CleanAndMalicious```) and open the C# file (the file name will be either ```DmzStorageFunction-CleanAndMalicious.cs``` or ```DmzStorageFunction-CleanOnly.cs```).

Open the .cs file in a text editor like Visual Studio Code or Notepad. Edit the Storage Account name variable/s with the name of the destination Storage Accounts. Save the file after making the edits.

For example, if the Storage Account deployed for the clean blobs is **mycleanstorageaccount**, update the code:

```c#
namespace DmzStorageFunction
{
    public static class DmzStorageFunction_CleanOnly
    {
        private const string CleanStorageAccount = "mycleanstorageaccount"; <--------------------------------HERE
        private const string AntimalwareScanEventType = "Microsoft.Security.MalwareScanningResult";
```

### 3. Upload edited code to your repo

Create a new public repo in your Github account. Upload the extracted files to the new repo (**Add file** -> **Upload files**). Only upload the files in the folder for the sample you will be using (folder name either: ```DmzStorageFunction-CleanOnly``` or ```DmzStorageFunction-CleanAndMalicious```)

![deployfunction3](/images/functionapp-create-3.png)

Confirm the files are uploaded. Then copy the url for your repo - like ```https://github.com/zcabrer/testrepo```

![deployfunction6](/images/functionapp-create-6.png)


### 4. Deploy code to the Function App using the Azure Portal

Now we will deploy the code to the Function App by pointing it to the repo URL copied in step 3. Navigate to the Function App in the Azure Portal and select **Deplolyment Center**. Select **External Git** from the dropdown. Enter your repo URL in **Repository** and select the branch (such as **master** or **main**). Click **Save**.

![functionapp-create-4](/images/functionapp-create-4.png)

It takes several minutes to deploy the code to the Function App. Navigate to the **Logs** tab to view the deployment status. The deployment should complete in 5-10 minutes then you will see success in the log.

![functionapp-create-5](/images/functionapp-create-5.png)

Verify the Functions is listed under **Functions**.

![confirm published function](images/deploy-function-code-confirm-deployed.png)

## Configure Event Subscription

After the Azure Function is published, create an Event Subscription in the Event Grid Custom Topic. The Event Subscription is the object that ensures the blob scan results from Defender are sent to the Function App for processing.

Navigate to the **Event Grid Custom Topic** -> **Event Subscriptions** -> click **+ Event Subscription** to create a new subscription.

![eventgrid-configure-eventsubscription-1](images/eventgrid-configure-eventsubscription-1.png)

Give the Event Subscription a friendly name. Under **Endpoint Details** select **Azure Function**. Select the items in the dropdowns to locate the Function App that was just published.

![eventgrid-configure-eventsubscription-2](images/eventgrid-configure-eventsubscription-2.png)

## Validation

To validate the solution, upload a clean and malicious blob to the DMZ Storage Account and ensure they are moved the appropriate storage account. Follow the validation steps in [this Microsoft Documentation](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-storage-test#testing-malware-scanning).

Use Function App log streaming in the [Azure Portal](https://learn.microsoft.com/en-us/azure/azure-functions/streaming-logs#built-in-log-streaming) or in [Visual Studio Code](https://learn.microsoft.com/en-us/azure/azure-functions/streaming-logs#visual-studio-code) to view live logs while the function executes. This is very helpful in debugging any errors or issues with the code.


# Resources
- The C# sample code in linked repos heavily leans on [this sample C# code](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-storage-configure-malware-scan#option-2-function-app-based-on-event-grid-events) from Defender for Storage Microsoft documentation and [this code from the Blob Storage SDK Documentation](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-copy-async-dotnet).
- [Malware Scanning in Defender for Storage](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-storage-malware-scan)
- [Setting up response to Malware Scanning](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-storage-configure-malware-scan)