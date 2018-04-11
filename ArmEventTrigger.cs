using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArmGrid
{
    public static class ArmEventTrigger
    {

        private static readonly HttpClient HttpClient = new HttpClient();
        //TODO: ad app settings reference
        private static readonly string EventGridEndpoint = "app setting variable";

        [FunctionName("ArmEventTrigger")]
        public static async Task <IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequestMessage req, TraceWriter log)
        {
            string requestContent = await req.Content.ReadAsStringAsync();

            //TODO: lookup response code for event grid to get EG to resubmit it since it failed delivery
            return await ReRaiseEventGridEvent(requestContent, log)
                ? (ActionResult)new OkObjectResult("")
                : new BadRequestObjectResult("It's busted");
        }

        private static async Task<bool> ReRaiseEventGridEvent(string originalEvent, TraceWriter log)
        {
            EventGridEvent[] eventGridEvents = JsonConvert.DeserializeObject<EventGridEvent[]>(originalEvent);

            foreach (EventGridEvent eventGridEvent in eventGridEvents)
            {
                if (eventGridEvent.EventType == "Microsoft.Resources.ResourceWriteSuccess") {
                    JObject dataObject = eventGridEvent.Data as JObject;
                    string subscriptionId = (string) dataObject["subscriptionId"];
                    string resourceUri = (string) dataObject["resourceUri"];
                    log.Info(subscriptionId);
                    var armData = await GetArmResource();
                    //append armData to the end of the Data object
                }

                var newEvent = new EventGridEvent()
                {
                    Topic = eventGridEvent.Topic,
                    Subject = eventGridEvent.Subject,
                    EventType = eventGridEvent.EventType,
                    EventTime = eventGridEvent.EventTime,
                    Id = eventGridEvent.Id,
                    DataVersion = eventGridEvent.DataVersion,
                    Data = eventGridEvent.Data
                };

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, EventGridEndpoint)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(newEvent), Encoding.UTF8, "application/json")
                };

                HttpResponseMessage response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Exception HTTP Response {response.StatusCode}");
                }
            }
            //TODO: return true if we are good return false if we want event grid the the resubmit
            return true;
        }

        private static async Task <string> GetArmResource()
        {
            //TODO: fix references and make this shizzle work
            var eventData = dataObject.ToObject<ResourceWriteSuccessData>();
				log.Info($"Got VM event data {eventData}");

				var azureServiceTokenProvider = new AzureServiceTokenProvider();

				try
				{
					var serviceCreds = new TokenCredentials(await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/").ConfigureAwait(false));

					var resourceManagementClient =
						new ResourceManagementClient(serviceCreds) { SubscriptionId = subscriptionId };

					var resourceState = await resourceManagementClient.Resources.GetByIdAsync(eventData.ResourceUri, "2017-12-01");
					var properties = resourceState.Properties.ToString()
						.Replace(Environment.NewLine, String.Empty)
						.Replace("\\", String.Empty).Replace(" ", String.Empty);

					log.Info(properties);
					response = properties;

				}
				catch (Exception exp)
				{
					log.Info($"Something went wrong: {exp.Message}");
				}
            return "";
        }
    }
}
