using System.IO;
using System.Collections.Generic;
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.Storage.Blob;
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
					string list_network_watchers = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/networkWatchers?api-version=2021-06-01";
					string list_nsgs = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/networkSecurityGroups?api-version=2021-06-01";
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
						    }
						    
						}
		            }
		            catch (System.Net.Http.HttpRequestException e)
		            {
		                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
		            }


		            ////// get all nsgs

		            client = new SingleHttpClientInstance();
		            try
		            {
		                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(list_nsgs, subs_id));
		                req.Headers.Accept.Clear();
		                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

		                if (response.IsSuccessStatusCode)
						{
						    string data =  await response.Content.ReadAsStringAsync();
						    var result = JsonConvert.DeserializeObject<NSGApiResult>(data);

                            string[] networkWatcherRegions = new string[0];
                            if( !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("nwRegions"))){
                                networkWatcherRegions = Environment.GetEnvironmentVariable("nwRegions").Split(',');
                            }
                            List<string> list_networkWatcherRegions = new List<string>(networkWatcherRegions);
                            log.LogInformation("list_networkWatcherRegions from above method : ");
                            for(int i=0;i<list_networkWatcherRegions.Count;i++)
                            {
                            log.LogInformation(list_networkWatcherRegions[i]);
                            }
                            log.LogInformation("--------------------------------------------------");
						   	await enable_flow_logs(result, nwList, token, subs_id, log,list_networkWatcherRegions);
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

        static async Task enable_flow_logs(NSGApiResult nsgresult, Dictionary<string, string> nwList, String token, String subs_id, ILogger log,List<string> networkWatcherRegions)
        {
        	
        	Dictionary<string, string> storageloc = new Dictionary<string, string>(); 
        	string[] all_locations = new string[]{"eastasia","southeastasia","centralus","eastus","eastus2","westus","northcentralus","southcentralus","northeurope","westeurope","japanwest","japaneast","brazilsouth","australiaeast","australiasoutheast","southindia","centralindia","westindia","canadacentral","canadaeast","uksouth","ukwest","westcentralus","westus2","koreacentral","koreasouth","francecentral","uaenorth","switzerlandnorth","norwaywest","germanywestcentral","swedencentral","jioindiawest","westus3","norwayeast","southafricanorth","australiacentral2","australiacentral","francesouth","qatarcentral"};
        	List<string> list_locations = new List<string>(all_locations);
        	log.LogInformation("network watcher Regions list from enable_flow_logs : ");
            for(int i=0;i<networkWatcherRegions.Count;i++)
            {
                log.LogInformation(networkWatcherRegions[i]);
            }
            log.LogInformation("--------------------------------------------------");
            log.LogInformation("NSG list from enable_flow_logs : ");
             foreach (var nsg in nsgresult.value) {
                log.LogInformation(nsg.location);
             }
             log.LogInformation("--------------------------------------------------");
        	foreach (var nsg in nsgresult.value) {
        		if(( networkWatcherRegions == null || networkWatcherRegions.Count == 0) || networkWatcherRegions.Contains(nsg.location) ){
                   	if(list_locations.Contains(nsg.location)){
                       try {
                       log.LogInformation(String.Format("check and create storage account for location  : {0}",nsg.location));
                               string loc_nw = nwList[nsg.location];
                               string storageId = "";
                               if(storageloc.ContainsKey(nsg.location)){
                                   storageId = storageloc[nsg.location];
                               }else{
                                   storageId = await check_avid_storage_account(token,subs_id,nsg.location,log);
                                   storageloc.Add(nsg.location, storageId);
                               }
                               log.LogInformation(String.Format("storageId :{0} for location  : {1}",storageId,nsg.location));
                               if(storageId.Equals("null")){
                                   break;
                               }

                               await check_and_enable_flow_request(nsg, storageId, loc_nw, subs_id, token, log);
                           } catch (Exception e) {
                               log.LogError(e, String.Format("Function UpdateNSGFlows is failed for Region : {0} is failing and subscriptionId : {1}",nsg.location ,subs_id));
                               log.LogError(e.Message);
                           }
                    }
                }
		   	}

		   	Dictionary<string, string> allnsgloc = new Dictionary<string, string>(); 
		   	foreach (var nsg in nsgresult.value) {
		   		if(!allnsgloc.ContainsKey(nsg.location)){
		   			allnsgloc.Add(nsg.location, "yes");
		   		}
		   	}

		   	foreach (string location_check in all_locations){
		   		if(!allnsgloc.ContainsKey(location_check)){
		   			await check_delete_storage_account(token, subs_id, location_check ,log);
		   		}
		   	}
        }

        static async Task<String> check_and_enable_flow_request(NetworkSecurityGroup nsg, String storageId, String loc_nw, String subs_id, String token, ILogger log){
        	string enable_flow_logs_url = "https://management.azure.com{0}/configureFlowLog?api-version=2021-06-01";
        	string query_flow_logs_url = "https://management.azure.com{0}/queryFlowLogStatus?api-version=2021-06-01";
        	

        	var client = new SingleHttpClientInstance();
        	try
            {

            	dynamic myObject = new JObject();
            	myObject.targetResourceId = nsg.id;
            	HttpRequestMessage checkReq = new HttpRequestMessage(HttpMethod.Post, String.Format(query_flow_logs_url, loc_nw));
            	var content = new StringContent(myObject.ToString(), Encoding.UTF8, "application/json");
            	checkReq.Content = content;
                checkReq.Headers.Accept.Clear();
                checkReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage check_response = await SingleHttpClientInstance.sendApiPostRequest(checkReq, token);
                
                if (check_response.IsSuccessStatusCode)
				{
				    string check_data =  await check_response.Content.ReadAsStringAsync();
				    var check_result = JsonConvert.DeserializeObject<FlowLogStatusResponse>(check_data);
				    if(check_result.properties.enabled){
				    	return "false";
				    }
				    
				} else{
					return "false";
				}


            	dynamic properties = new JObject();
            	properties.storageId = storageId;
            	properties.enabled = true;
            	dynamic retention = new JObject();
            	retention.days = 1;
            	retention.enabled = true;
            	properties.retentionPolicy = retention;
            	myObject.properties = properties;
            	content = new StringContent(myObject.ToString(), Encoding.UTF8, "application/json");
            	
                
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, String.Format(enable_flow_logs_url, loc_nw));
                req.Content = content;
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);
                
                if (response.IsSuccessStatusCode)
				{
				    string data =  await response.Content.ReadAsStringAsync();
				    return "true";
				    
				}

            } 
            catch (System.Net.Http.HttpRequestException e)
            {
                log.LogInformation("Ignore. Failed for some region");
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
