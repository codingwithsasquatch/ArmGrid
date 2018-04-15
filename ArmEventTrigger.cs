using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Collections.Generic;
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
using Microsoft.Rest;
using System.Configuration;

namespace ArmGrid
{
	public static class ArmEventTrigger
	{
		private static readonly HttpClient HttpClient = new HttpClient();
		private static readonly string EventGridEndpoint = Environment.GetEnvironmentVariable("EventGridCustomTopicEndpoint");
		
		[FunctionName("ArmEventTrigger")]
		public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequestMessage req, TraceWriter log)
		{
			string requestContent = await req.Content.ReadAsStringAsync();

			//validate subscription if this is a validation request
			if (req.Headers.TryGetValues("Aeg-Event-Type", out IEnumerable<string> headerValues))
			{
				string validationHeaderValue = headerValues.FirstOrDefault<string>();
				if(validationHeaderValue == "SubscriptionValidation")
				{
					EventGridEvent[] events = JsonConvert.DeserializeObject<EventGridEvent[]>(requestContent);
					JObject dataObject = events.FirstOrDefault().Data as JObject;
					return new OkObjectResult(new { validationResponse = dataObject["validationCode"] });
				}
			}

			return await ReRaiseEventGridEvent(requestContent, log)
				? (ActionResult)new OkResult()
				: new StatusCodeResult(500);
		}

		private static async Task<bool> ReRaiseEventGridEvent(string originalEvent, TraceWriter log)
		{
			EventGridEvent[] eventGridEvents = JsonConvert.DeserializeObject<EventGridEvent[]>(originalEvent);

			foreach (EventGridEvent eventGridEvent in eventGridEvents)
			{
				var newEvent = eventGridEvent;
				JObject dataObject = eventGridEvent.Data as JObject;

				if (eventGridEvent.EventType == "Microsoft.Resources.ResourceWriteSuccess")
				{
					string subscriptionId = (string)dataObject["subscriptionId"];
					string resourceUri = (string)dataObject["resourceUri"];
					log.Info(subscriptionId);
					var resourceState = await GetArmResourceState(subscriptionId, resourceUri, log);
					
					dataObject.Add("ResourceState", resourceState);
					newEvent.Data = dataObject;
				}

				HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, EventGridEndpoint)
				{
					Content = new StringContent(JsonConvert.SerializeObject(newEvent), Encoding.UTF8, "application/json")
				};

				HttpResponseMessage response = await HttpClient.SendAsync(request);
				if (!response.IsSuccessStatusCode)
				{
					return false;
					//throw new Exception($"Exception HTTP Response {response.StatusCode}");
				}
			}
			return true;
		}

		private static async Task<string> GetArmResourceState(string subscriptionId, string resourceId, TraceWriter log)
		{
			var azureServiceTokenProvider = new AzureServiceTokenProvider();

			try
			{
				var serviceCreds = new TokenCredentials(await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/").ConfigureAwait(false));
				var resourceManagementClient = new ResourceManagementClient(serviceCreds) { SubscriptionId = subscriptionId };
				var resourceState = resourceManagementClient.Resources.GetById(resourceId, "2017-05-10");
				var properties = resourceState.Properties.ToString();
				//	.Replace(Environment.NewLine, String.Empty)
				//	.Replace("\\", String.Empty).Replace(" ", String.Empty);

				log.Info(properties);
				return properties;
			}
			catch (Exception exp)
			{
				var message = $"Something went wrong: {exp.Message}";
				log.Info(message);
				return message;
			}
		}
	}
}
