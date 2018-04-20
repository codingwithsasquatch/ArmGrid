using System;
using System.Text;
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

namespace ArmGrid
{
	public static class ArmEventTrigger
	{
		private static readonly HttpClient HttpClient = new HttpClient();
		private static readonly string EventGridEndpoint = Environment.GetEnvironmentVariable("EventGridCustomTopicEndpoint");

		private static List<Provider> ResourceProviders;
		
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
					string subscriptionId = (string) dataObject["subscriptionId"];
					string resourceUri = (string) dataObject["resourceUri"];
					
					string resourceProvider = (string) dataObject["resourceProvider"];
					log.Info(subscriptionId);
					var resourceState = await GetArmResourceState(subscriptionId, resourceProvider, resourceUri, log);
					
					dataObject.Add("ResourceState", JObject.Parse(resourceState));
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

		private static async Task<string> GetArmResourceState(string subscriptionId, string resourceProvider, string resourceId, TraceWriter log)
		{
			var azureServiceTokenProvider = new AzureServiceTokenProvider();

			try
			{
				var serviceCreds = new TokenCredentials(await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/").ConfigureAwait(false));
				var resourceManagementClient = new ResourceManagementClient(serviceCreds) { SubscriptionId = subscriptionId };

				if (ResourceProviders != null) {
					ResourceProviders = (List<Provider>) await resourceManagementClient.Providers.ListAsync();
				}

				var resource = new Resource(resourceId);
				var apiVersion = ResourceProviders
					.Where( o => o.NamespaceProperty == resourceProvider)
					.Select( m => m.ResourceTypes)
					.First()
					.Where( p => p.ResourceType == resource.Type )
					.First().ApiVersions[0];
				var resourceState = resourceManagementClient.Resources.GetById(resourceId, apiVersion);
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

	public class Resource
	{
		public Resource(string resourceId)
		{
			Id = resourceId;
		}
		public string Id
		{
			get { return Id; }
			set
			{ 
				Id = value;
				ExtractResourceProps();
			}
		}
		public string Subscription {get; private set;}
		public string Provider {get; private set;}
		public string Type {get; private set;}
		public string Name {get; private set;}
		public string ResourceGroup {get; private set;}

		private void ExtractResourceProps()
		{
			Uri resourceUri = new Uri("https://management.azure.com"+Id);
			Subscription = resourceUri.Segments[1];
			ResourceGroup = resourceUri.Segments[3];
			Provider = resourceUri.Segments[5];
			
			if (resourceUri.Segments.Length>6)
			{
				string resourceType = resourceUri.Segments[6];
				for (int i=8; i<resourceUri.Segments.Length; i+=2)
				{
					resourceType += resourceUri.Segments[i];
				}
				Type = resourceType;
			}
		}
	}
}
