using System.IO;
using System.Collections.Generic;
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Threading;
using System.Linq;

namespace NwNsgProject
{
    public static class UpdateNSGFlows
    {


		[FunctionName("UpdateNSGFlows")]
		public static async Task Run([TimerTrigger("0 */3 * * * *")] TimerInfo myTimer, ILogger log)
		{
		    try
		    {
			    var secret = Environment.GetEnvironmentVariable("MSI_SECRET");
	            var identity_header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
	            var principal_id = Environment.GetEnvironmentVariable("PRINCIPAL_ID");

			    var subs_ids = Environment.GetEnvironmentVariable("subscriptionIds").Split(',');
			    string token = null;

			    UriBuilder builder = new UriBuilder(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
				string apiversion = Uri.EscapeDataString("2017-09-01");
				string resource = Uri.EscapeDataString("https://management.azure.com/");
				builder.Query = "api-version="+apiversion+"&resource="+resource;

	            if(principal_id != null){
	                builder = new UriBuilder(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"));
	                apiversion = Uri.EscapeDataString("2019-08-01");
	                builder.Query = "api-version="+apiversion+"&resource="+resource+"&principal_id="+principal_id;
	            }

				var client = new SingleHttpClientInstance();
	            try
	            {
	                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
	                req.Headers.Accept.Clear();
	                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	                req.Headers.Add("secret", secret);
	                if(principal_id != null){
	                    req.Headers.Add("X-IDENTITY-HEADER",identity_header);
	                }

	                HttpResponseMessage response = await SingleHttpClientInstance.getToken(req);
	                if (response.IsSuccessStatusCode)
					{
					    string data =  await response.Content.ReadAsStringAsync();
					    var tokenObj = JsonConvert.DeserializeObject<Token>(data);
					    token = tokenObj.access_token;
					}
	            }
	            catch (System.Net.Http.HttpRequestException e)
	            {
	                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
	            }

	            foreach(var subs_id in subs_ids){
		            ////// get network watchers first

					Dictionary<string, string> nwList = new Dictionary<string, string>();
					Dictionary<string, string> nwListName = new Dictionary<string, string>();
					string list_network_watchers = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/networkWatchers?api-version=2021-06-01";
//					string list_nsgs = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/networkSecurityGroups?api-version=2021-06-01";
					string list_vnet = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/virtualNetworks?api-version=2024-05-01";
					client = new SingleHttpClientInstance();
		            try
		            {
		                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(list_network_watchers, subs_id));
		                req.Headers.Accept.Clear();
		                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

		                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);
		                if (response.IsSuccessStatusCode)
						{
						    string data =  await response.Content.ReadAsStringAsync();
						    var result = JsonConvert.DeserializeObject<NWApiResult>(data);

						    foreach (var nw in result.value) {
						    	nwList.Add(nw.location,nw.id);
						    	nwListName.Add(nw.location,nw.name);
						    }

						}
		            }
		            catch (System.Net.Http.HttpRequestException e)
		            {
		                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
		            }


		            ////// get all vnet

		            client = new SingleHttpClientInstance();
		            try
		            {
		                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(list_vnet, subs_id));
		                req.Headers.Accept.Clear();
		                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

		                if (response.IsSuccessStatusCode)
						{
						    string data =  await response.Content.ReadAsStringAsync();
						    var result = JsonConvert.DeserializeObject<VNETApiResult>(data);

                            string[] networkWatcherRegions = new string[0];
                            if( !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("nwRegions"))){
                                networkWatcherRegions = Environment.GetEnvironmentVariable("nwRegions").Split(',');
                            }
                            List<string> list_networkWatcherRegions = new List<string>(networkWatcherRegions);
						   	await enable_flow_logs(result, nwList, token, subs_id, log,list_networkWatcherRegions, nwListName);
						   	log.LogInformation("Added the flow logs for vnet successfully");
						}
		            }
		            catch (System.Net.Http.HttpRequestException e)
		            {
		                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
		            }
		        }
		    }
		    catch (Exception e)
		    {
		        log.LogError(e, "Function UpdateNSGFlows is failed to process request");
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

            public static async Task<HttpResponseMessage> getToken(HttpRequestMessage req)
            {
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }

            public static async Task<HttpResponseMessage> sendApiRequest(HttpRequestMessage req, String token)
            {
            	HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }
            public static async Task<HttpResponseMessage> sendApiPostRequest(HttpRequestMessage req, String token)
            {
            	HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }

        }

        static async Task enable_flow_logs(VNETApiResult vnetresult, Dictionary<string, string> nwList, String token, String subs_id, ILogger log,List<string> networkWatcherRegions, Dictionary<string, string> nwListName)
        {
            log.LogInformation("Entered into the enable flow logs function");
        	Dictionary<string, string> storageloc = new Dictionary<string, string>();
        	string[] all_locations = new string[]{"eastasia","southeastasia","centralus","eastus","eastus2","westus","northcentralus","southcentralus","northeurope","westeurope","japanwest","japaneast","brazilsouth","australiaeast","australiasoutheast","southindia","centralindia","westindia","canadacentral","canadaeast","uksouth","ukwest","westcentralus","westus2","koreacentral","koreasouth","francecentral","uaenorth","switzerlandnorth","norwaywest","germanywestcentral","swedencentral","jioindiawest","westus3","norwayeast","southafricanorth","australiacentral2","australiacentral","francesouth","qatarcentral"};
        	List<string> list_locations = new List<string>(all_locations);
        	foreach (var vnet in vnetresult.value) {
        		if(( networkWatcherRegions == null || networkWatcherRegions.Count == 0) || networkWatcherRegions.Contains(vnet.location) ){
                   	if(list_locations.Contains(vnet.location)){
                       try {
                               string loc_nw = nwList[vnet.location];
                               string nw_name = nwListName[vnet.location];
                               string storageId = "";
                               if(storageloc.ContainsKey(vnet.location)){
                                   storageId = storageloc[vnet.location];
                               }else{
                                   storageId = await check_avid_storage_account(token,subs_id,vnet.location,log);
                                   storageloc.Add(vnet.location, storageId);
                               }
                               if(storageId.Equals("null")){
                                   break;
                               }
                               string resourceGroup = extractResourceGroupName(vnet.id);
                               await check_and_enable_flow_request(vnet, resourceGroup, storageId, loc_nw, nw_name, subs_id, token, log);
                               log.LogInformation("Completed the check and enable flow request function successfully");
                           } catch (System.Net.Http.HttpRequestException e) {
                               log.LogError(e, String.Format("Function UpdateNSGFlows is failed for Region : {0} is failing and subscriptionId : {1}",vnet.location ,subs_id));
                           }
                    }
                }
		   	}

		   	Dictionary<string, string> allnsgloc = new Dictionary<string, string>();
		   	foreach (var vnet in vnetresult.value) {
		   		if(!allnsgloc.ContainsKey(vnet.location)){
		   			allnsgloc.Add(vnet.location, "yes");
		   		}
		   	}

		   	foreach (string location_check in all_locations){
		   		if(!allnsgloc.ContainsKey(location_check)){
		   			await check_delete_storage_account(token, subs_id, location_check ,log);
		   		}
		   	}
        }

        static string extractResourceGroupName(string id){
            int startIndex = id.IndexOf("resourceGroups/", StringComparison.OrdinalIgnoreCase) + "resourceGroups/".Length;
            int endIndex = id.IndexOf("/", startIndex);
            return id.Substring(startIndex, endIndex - startIndex);
        }

        static async Task<String> check_and_enable_flow_request(VirtualNetwork vnet, String resourceGroupName, String storageId, String loc_nw, String nw_name, String subs_id, String token, ILogger log){
        	log.LogInformation("Entered into the check and enable flow request function");
            string query_flow_logs_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Network/networkWatchers/{2}/flowLogs?api-version=2024-05-01";
            string enable_flow_logs_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Network/networkWatchers/{2}/flowLogs/{3}?api-version=2024-05-01";
            log.LogInformation("Entered into the check and enable flow request function");

        	var client = new SingleHttpClientInstance();
        	try
            {
                String nwResourceGroup = "NetworkWatcherRG";
				dynamic myObject = new JObject();
            	HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(query_flow_logs_url, subs_id, nwResourceGroup, nw_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

                log.LogInformation("Entered into the check and enable flow request function inside try");
                log.LogInformation("Response status of query flow log api: {IsSuccessStatusCode}", response.IsSuccessStatusCode);
                if (response.IsSuccessStatusCode)
				{
				    log.LogInformation("Response status of query flow log api is true");
				    string check_data =  await response.Content.ReadAsStringAsync();
				    var json = JObject.Parse(check_data);
				    HashSet<string> enabledVnetIds = new HashSet<string>();
				    foreach (var flowLog in json["value"]){
				        var targetResourceId = flowLog["properties"]["targetResourceId"].ToString();
                        if(targetResourceId.Contains("virtualNetworks", StringComparison.OrdinalIgnoreCase)){
                            enabledVnetIds.Add(targetResourceId);
                        }
				    }
				    foreach (var item in enabledVnetIds)
                    {
                        log.LogInformation("Value in Hashset: {Item}", item);
                    }
				    if(!enabledVnetIds.Any(id => string.Equals(id, vnet.id, StringComparison.OrdinalIgnoreCase))){
				        log.LogInformation("Vnet id: {vnet.id}", vnet.id);
				        dynamic properties = new JObject();
                        properties.storageId = storageId;
                        properties.targetResourceId = vnet.id;
                        properties.enabled = true;
                        dynamic retention = new JObject();
                        retention.days = 1;
                        retention.enabled = true;
                        properties.retentionPolicy = retention;
                        myObject.properties = properties;
                        var content = new StringContent(myObject.ToString(), Encoding.UTF8, "application/json");

                        log.LogInformation("Storage account id: {storageId}", storageId);

                        log.LogInformation( "Entered into the check and enable flow request function and enabling the flow logs for this vnet");
                        string flowLogName = vnet.name + "-" + resourceGroupName + "-flowlogs";

                        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, String.Format(enable_flow_logs_url, subs_id, resourceGroupName, nw_name, flowLogName));
                        request.Content = content;
                        request.Headers.Accept.Clear();
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                        HttpResponseMessage responseApi = await SingleHttpClientInstance.sendApiPostRequest(request, token);

                        if (responseApi.IsSuccessStatusCode)
                        {
                            log.LogInformation("enabling flow log is succeeded with api ");
                            string data =  await responseApi.Content.ReadAsStringAsync();
                        	return "true";

                        }
				    }
				} else{
					return "false";
				}
            }
            catch (System.Net.Http.HttpRequestException e)
            {
                log.LogInformation(e, "Ignore. Failed for some region");
            }
            return "false";
        }

        static async Task<String> check_avid_storage_account( String token, String subs_id, String location ,ILogger log){
        	string fetch_storage_account_details = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2021-08-01";
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");

        	var location_codes_string = @"{""uaenorth"":""uan1"",""switzerlandnorth"":""swn1"",""norwaywest"":""nrw1"",""germanywestcentral"":""gwc1"",""eastasia"":""eea1"",""southeastasia"":""sea1"",""centralus"":""ccu1"",""eastus"":""eeu1"",""eastus2"":""eeu2"",""westus"":""wwu1"",""northcentralus"":""ncu1"",""southcentralus"":""scu1"",""northeurope"":""nne1"",""westeurope"":""wwe1"",""japanwest"":""wwj1"",""japaneast"":""eej1"",""brazilsouth"":""ssb1"",""australiaeast"":""eau1"",""australiasoutheast"":""sau1"",""southindia"":""ssi1"",""centralindia"":""cci1"",""westindia"":""wwi1"",""canadacentral"":""ccc1"",""canadaeast"":""eec1"",""uksouth"":""suk1"",""ukwest"":""wuk1"",""westcentralus"":""wcu1"",""westus2"":""wwu2"",""koreacentral"":""cck1"",""koreasouth"":""ssk1"",""francecentral"":""ccf1"",""francesouth"":""ssf1"",""australiacentral"":""cau1"",""australiacentral2"":""cau2"",""westus3"":""wwu3"",""southafricanorth"":""nsa1"",""norwayeast"":""enr1"",""swedencentral"":""csn1"",""jioindiawest"":""wwi2"",""qatarcentral"":""cqr1""}";

        	var location_codes = JsonConvert.DeserializeObject<Dictionary<string, string>>(location_codes_string);

        	var subscription_tag = subs_id.Replace("-","").Substring(0,8) + customerid.Replace("-","").Substring(0,8);
        	var storage_account_name = "avi"+local+subscription_tag+location_codes[location];

        	string appNameStage1 = local + "AvidFlowLogs" + subscription_tag  + location_codes[location];
        	string storage_account_name_activity = local + "avidact" + subscription_tag;

        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(fetch_storage_account_details, subs_id, resourceGroup, storage_account_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

                if (response.IsSuccessStatusCode)
				{
				    string data =  await response.Content.ReadAsStringAsync();
				    var result = JsonConvert.DeserializeObject<StorageAccountProp>(data);
				    var is_deployment = await check_app_deployment(token, appNameStage1, subs_id, log);
				    if(!is_deployment){
				    	await listKeys(token, storage_account_name, storage_account_name_activity, appNameStage1, subs_id, log);
				    }
				    return result.id;

				}
				else{
					await create_resources(token, subs_id, location_codes[location], storage_account_name, location, log);
					return "null";
				}
        	}
        	catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task create_retention_policy(String token, String subs_id, String storage_account_name, ILogger log){
        	string policy_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}/managementPolicies/default?api-version=2021-08-01";
			string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string filled_url = String.Format(policy_url, subs_id, resourceGroup, storage_account_name);
        	string retention_policy_string = @"{""properties"": { ""policy"": { ""rules"": [ { ""name"": ""Sophosflowlogsdelete"", ""enabled"": true, ""type"": ""Lifecycle"", ""definition"": { ""filters"": { ""blobTypes"": [ ""blockBlob"" ] }, ""actions"": { ""baseBlob"": { ""delete"": { ""daysAfterModificationGreaterThan"": 1 } }, ""snapshot"": { ""delete"": { ""daysAfterCreationGreaterThan"": 1 } } } } } ] } } }";
        	var policy_json = JsonConvert.DeserializeObject<StorageAccountRentention>(retention_policy_string);

        	try{

	        	HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, filled_url);
	            req.Headers.Accept.Clear();
	            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

	            var content = new StringContent(JsonConvert.SerializeObject(policy_json), Encoding.UTF8, "application/json");
	            req.Content = content;

			    HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);

	        }
	        catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("retention policy request failed ?", e);
            }

        }

        static async Task create_resources( String token, String subs_id, String location_code, String storage_account_name, String location, ILogger log){

        	string create_storage_account_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2021-08-01";

        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");
        	string storageAccountConnecion = Util.GetEnvironmentVariable("storageAccountConnecion");
        	string avidAddress = Util.GetEnvironmentVariable("avidFlowAddress");
        	string storageSku = Util.GetEnvironmentVariable("storageSku");

        	var subscription_tag = subs_id.Replace("-","").Substring(0,8) + customerid.Replace("-","").Substring(0,8);
        	string appNameStage1 = local + "AvidFlowLogs" + subscription_tag  + location_code;
        	string storage_account_name_activity = local + "avidact" + subscription_tag;

        	string storage_json_string = @"{""sku"": {""name"": ""Standard_GRS""}, ""kind"": ""StorageV2"", ""location"": ""eastus"", ""properties"": {""allowBlobPublicAccess"": false}}";
        	var storage_json = JsonConvert.DeserializeObject<StorageAccountPutObj>(storage_json_string);
        	storage_json.location = location;
        	if(!string.IsNullOrEmpty(storageSku)){
        		storage_json.sku.name = storageSku;
        	}
        	string filled_url = String.Format(create_storage_account_url, subs_id, resourceGroup, storage_account_name);
        	try{

	        	HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, filled_url);
	            req.Headers.Accept.Clear();
	            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

	            var content = new StringContent(JsonConvert.SerializeObject(storage_json), Encoding.UTF8, "application/json");
	            req.Content = content;

			    HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);

	            if (response.IsSuccessStatusCode)
	            {

	            	int milliseconds = 80000;
					Thread.Sleep(milliseconds);

					await create_retention_policy(token, subs_id, storage_account_name, log);
					await listKeys(token, storage_account_name, storage_account_name_activity, appNameStage1, subs_id, log);

	            }

	        }
	        catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task listKeys(String token, String storage_account_name, String storage_account_name_activity, String appNameStage1, String subs_id, ILogger log){
        	string list_storage_account_keys = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}/listKeys?api-version=2021-08-01";
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");

            HttpRequestMessage list_req = new HttpRequestMessage(HttpMethod.Post, String.Format(list_storage_account_keys, subs_id, resourceGroup, storage_account_name));
            list_req.Headers.Accept.Clear();
            list_req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response_keys = await SingleHttpClientInstance.sendApiRequest(list_req, token);

            string data_check =  await response_keys.Content.ReadAsStringAsync();

            if (response_keys.IsSuccessStatusCode)
			{
			    string data =  await response_keys.Content.ReadAsStringAsync();
			    var result = JsonConvert.DeserializeObject<StorageAccountKeyList>(data);
			    string accountkey = "";
			   	foreach(var key in result.keys){
			   		accountkey = key.value;
			   	}

			   	Boolean check_deploy = await deploy_app(token, accountkey , storage_account_name, storage_account_name_activity, appNameStage1, subs_id, log);
			}

        }

        static async Task<Boolean> deploy_app(String token, String accountkey , String storage_account_name, String storage_account_name_activity, String appNameStage1, String subs_id, ILogger log){

        	string connectionString = "DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}";
        	string create_deployment_url = "https://management.azure.com/subscriptions/{0}/resourcegroups/{1}/providers/Microsoft.Resources/deployments/{2}?api-version=2021-04-01";
        	string filledConnectionString = String.Format(connectionString, storage_account_name, accountkey);
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");
        	string storageAccountConnecion = Util.GetEnvironmentVariable("storageAccountConnecion");
        	string avidAddress = Util.GetEnvironmentVariable("avidFlowAddress");
			string branch = Util.GetEnvironmentVariable("branch");

		   	string deployment_json_string = @"{""properties"": {""templateLink"": {""uri"": ""https://s3-us-west-2.amazonaws.com/avidcore/azure/azureFlowDeploy.json"",""contentVersion"": ""1.0.0.0""},""mode"": ""Incremental"",""parameters"": {""customerId"": {""value"": ""null""},""nsgSourceDataConnection"":{""value"":""null""},""storageAccountName"":{""value"":""null""},""storageAccountConnecion"":{""value"":""null""},""appName"":{""value"":""null""},""avidAddress"":{""value"":""null""},""branch"":{""value"":""null""},""hostId"":{""value"":""null""} } } }";

		    var deployment_json = JsonConvert.DeserializeObject<WebAppDeployment>(deployment_json_string);

		    deployment_json.properties.templateLink.uri = Util.GetEnvironmentVariable("flowDepSource");
		    deployment_json.properties.parameters.customerId.value = customerid;
		    deployment_json.properties.parameters.nsgSourceDataConnection.value = filledConnectionString;
		    deployment_json.properties.parameters.storageAccountName.value = storage_account_name_activity;
		    deployment_json.properties.parameters.appName.value = appNameStage1;
		    deployment_json.properties.parameters.storageAccountConnecion.value = storageAccountConnecion;
		    deployment_json.properties.parameters.avidAddress.value = avidAddress;
			deployment_json.properties.parameters.branch.value = branch;
			deployment_json.properties.parameters.hostId.value = Guid.NewGuid().ToString().Replace("-","");

		    string filled_url = String.Format(create_deployment_url, subs_id, resourceGroup, appNameStage1);

		    try{
			    HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, filled_url);
	            req.Headers.Accept.Clear();
	            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

	            var content = new StringContent(JsonConvert.SerializeObject(deployment_json), Encoding.UTF8, "application/json");
	            req.Content = content;

			    HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);

			    var check_resp = await response.Content.ReadAsStringAsync();

			    if (response.IsSuccessStatusCode){
			    	return true;
			    }
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
		    return false;
        }

        static async Task<Boolean> check_app_deployment(String token, String appNameStage1, String subs_id, ILogger log){
        	string check_deployment_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Web/sites/{2}/functions?api-version=2021-03-01";
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(check_deployment_url, subs_id, resourceGroup, appNameStage1));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);


                if (response.IsSuccessStatusCode)
				{
				    return true;
				}
        	}
        	catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
            return false;

        }

        static async Task remove_resources(String storage_account_name, String app_name, String token, String subs_id, String location,ILogger log){
        	string storage_account_delete_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2021-08-01";
        	string webapp_delete_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Web/sites/{2}?deleteMetrics=true&deleteEmptyServerFarm=true&api-version=2021-03-01";
			string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");

        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, String.Format(storage_account_delete_url, subs_id, resourceGroup, storage_account_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);


			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }

            try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, String.Format(webapp_delete_url, subs_id, resourceGroup, app_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);


			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task check_delete_storage_account( String token, String subs_id, String location ,ILogger log){
        	string fetch_storage_account_details = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2021-08-01";
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");

        	var location_codes_string = @"{""uaenorth"":""uan1"",""switzerlandnorth"":""swn1"",""norwaywest"":""nrw1"",""germanywestcentral"":""gwc1"",""eastasia"":""eea1"",""southeastasia"":""sea1"",""centralus"":""ccu1"",""eastus"":""eeu1"",""eastus2"":""eeu2"",""westus"":""wwu1"",""northcentralus"":""ncu1"",""southcentralus"":""scu1"",""northeurope"":""nne1"",""westeurope"":""wwe1"",""japanwest"":""wwj1"",""japaneast"":""eej1"",""brazilsouth"":""ssb1"",""australiaeast"":""eau1"",""australiasoutheast"":""sau1"",""southindia"":""ssi1"",""centralindia"":""cci1"",""westindia"":""wwi1"",""canadacentral"":""ccc1"",""canadaeast"":""eec1"",""uksouth"":""suk1"",""ukwest"":""wuk1"",""westcentralus"":""wcu1"",""westus2"":""wwu2"",""koreacentral"":""cck1"",""koreasouth"":""ssk1"",""francecentral"":""ccf1"",""francesouth"":""ssf1"",""australiacentral"":""cau1"",""australiacentral2"":""cau2"",""westus3"":""wwu3"",""southafricanorth"":""nsa1"",""norwayeast"":""enr1"",""swedencentral"":""csn1"",""jioindiawest"":""wwi2"",""qatarcentral"":""cqr1""}";

        	var location_codes = JsonConvert.DeserializeObject<Dictionary<string, string>>(location_codes_string);

        	var subscription_tag = subs_id.Replace("-","").Substring(0,8) + customerid.Replace("-","").Substring(0,8);
        	var storage_account_name = "avi"+local+subscription_tag+location_codes[location];

        	string appNameStage1 = local + "AvidFlowLogs" + subscription_tag  + location_codes[location];


        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(fetch_storage_account_details, subs_id, resourceGroup, storage_account_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

                if (response.IsSuccessStatusCode)
				{

					string data =  await response.Content.ReadAsStringAsync();
				    var result = JsonConvert.DeserializeObject<StorageAccountProp>(data);
				    await remove_resources(storage_account_name, appNameStage1, token, subs_id, location, log);
				}
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }
	}
}
