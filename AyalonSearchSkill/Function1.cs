using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AyalonSkill
{
    /// <summary>
    /// Sample custom skill that wraps the Bing entity search API to connect it with a 
    /// AI enrichment pipeline.
    /// </summary>
    public static class AyalonEntitySearch
    {
        #region Credentials
        // IMPORTANT: Make sure to enter your credential and to verify the API endpoint matches yours.
        // static readonly string bingApiEndpoint = "https://api.cognitive.microsoft.com/bing/v7.0/entities/";
        static readonly string ayalonEndpoint = "http://localhost:5000";
        static readonly string textToAgeEndpoint = "https://ariesearchutils.azurewebsites.net/api/TextToAge?code=pjqPR4X3Z2A1WCtduxp384Fg8Ys1kK7KFBbHorcAER4xlD3VdSQczA%3D%3D";
        #endregion

        #region Class used to deserialize the request
        public class InputRecord
        {
            public class InputRecordData
            {
                public string Document { get; set; }
            }
            public string RecordId { get; set; }
            public InputRecordData Data { get; set; }
        }
        
        public class AyalonInputRecord
        {
            public AyalonDocument[] Documents { get; set; }
        }
        public class AyalonDocument
        {
            public string Id { get; set; }
            public string Text { get; set; }
        }

        private class WebApiRequest
        {
            public List<InputRecord> Values { get; set; }
        }
        #endregion

        #region Classes used to serialize the response

        private class OutputRecord
        {
            public class OutputRecordData
            {
                public string[] entityTypes { get; set; }
                public string[] concepts { get; set; }
                public string[] relations { get; set; }
                public int age { get; set; }
            }

            public class OutputRecordMessage
            {
                public string Message { get; set; }
            }

            public string RecordId { get; set; }
            public OutputRecordData Data { get; set; }
            public List<OutputRecordMessage> Errors { get; set; }
            public List<OutputRecordMessage> Warnings { get; set; }
        }

        private class WebApiResponse
        {
            public List<OutputRecord> Values { get; set; }
        }
        #endregion

        #region Classes used to interact with the Ayalon API

        public class AyalonResponse
        {
            public AyalonEntity[] Entities { get; set; }
            public string Id { get; set; }
            public string Text { get; set; }
            public Relation[] Relations { get; set; }
        }

        public class Relation
        {
            public string RelationType { get; set; }
        }

        public class AyalonEntity
        {
            public int StartPosition { get; set; }
            public int EndPosition { get; set; }
            public string EntityType { get; set; }
            public double Score { get; set; }
            public Linking[] Linking { get; set; }
        }

        public class Linking
        {
            public string Concept_Id { get; set; }
            public string Source { get; set; }
        }

        public class TextToAgeResponse
        {
            public int Age { get; set; }
        }
        #endregion

        #region The Azure Function definition

        [FunctionName("EntitySearch")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Entity Search function: C# HTTP trigger function processed a request.");

            var response = new WebApiResponse
            {
                Values = new List<OutputRecord>()
            };

            string requestBody = new StreamReader(req.Body).ReadToEnd();
            var data = JsonConvert.DeserializeObject<WebApiRequest>(requestBody);

            // Do some schema validation
            if (data == null)
            {
                return new BadRequestObjectResult("The request schema does not match expected schema.");
            }
            if (data.Values == null)
            {
                return new BadRequestObjectResult("The request schema does not match expected schema. Could not find values array.");
            }

            // Calculate the response for each value.
            foreach (var record in data.Values)
            {
                if (record == null || record.RecordId == null) continue;

                OutputRecord responseRecord = new OutputRecord
                {
                    RecordId = record.RecordId
                };

                try
                {
                    responseRecord.Data = await GetEntityMetadata(record);
                }
                catch (Exception e)
                {
                    // Something bad happened, log the issue.
                    var error = new OutputRecord.OutputRecordMessage
                    {
                        Message = e.Message
                    };

                    responseRecord.Errors = new List<OutputRecord.OutputRecordMessage>
                    {
                        error
                    };
                }
                finally
                {
                    response.Values.Add(responseRecord);
                }
            }

            return (ActionResult)new OkObjectResult(response);
        }

        #endregion

        #region Methods to call the Bing API
        /// <summary>
        /// Gets metadata for a particular entity based on its name using Ayalon
        /// </summary>
        /// <param name="entityName">The name of the entity to extract data for.</param>
        /// <returns>Asynchronous task that returns entity data. </returns>
        private async static Task<OutputRecord.OutputRecordData> GetEntityMetadata(InputRecord inputRecord)
        {
            var uri = ayalonEndpoint + "/query";

            var result = new OutputRecord.OutputRecordData();

            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(uri)
            })
            {
                var serializerSettings = new JsonSerializerSettings();
                serializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                var data = new AyalonInputRecord { Documents = new AyalonDocument[] { new AyalonDocument { Id = inputRecord.RecordId, Text = inputRecord.Data.Document } } };
                var json = JsonConvert.SerializeObject(data, serializerSettings);
                var stringContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                request.Content = stringContent;

                HttpResponseMessage response = await client.SendAsync(request);
                string responseBody = await response?.Content?.ReadAsStringAsync();

                AyalonResponse[] ayalonResponses = JsonConvert.DeserializeObject<AyalonResponse[]>(responseBody);
                if (ayalonResponses != null)
                {
                    return AddEnrichedMetadata(ayalonResponses[0], inputRecord.Data.Document).Result;
                }
            }

            return result;
        }

        private async static Task<OutputRecord.OutputRecordData> AddEnrichedMetadata(AyalonResponse ayalonResponse, string text)
        {
            List<string> entityTypes = new List<string>();
            List<string> concepts = new List<string>();
            List<string> relations = new List<string>();
            int age = 0;

            if (ayalonResponse.Entities  != null)
            {
                foreach (AyalonEntity entity in ayalonResponse.Entities)
                {
                    entityTypes.Add(entity.EntityType);
                    if (entity.EntityType == "AGE")
                    {
                        string ageText = text.Substring(entity.StartPosition, entity.EndPosition - entity.StartPosition);
                        age = (await ResolveAgeEntity(ageText)).Age;
                    }
                    if (entity.Linking != null)
                    {
                        var umlsConcept = entity.Linking.FirstOrDefault(e => { return e.Source == "UMLS" && e.Concept_Id != null; });
                        if (umlsConcept != null)
                        {
                            concepts.Add("UMLS " + umlsConcept.Concept_Id + " (" + text.Substring(entity.StartPosition, entity.EndPosition - entity.StartPosition) + ")");
                        }
                    }
                }
            }
            if (ayalonResponse.Relations != null)
            {
                foreach(Relation relation in ayalonResponse.Relations)
                {
                    if (relation.RelationType != null)
                    {
                        relations.Add(relation.RelationType);
                    }
                }
            }

            var rootObject = new OutputRecord.OutputRecordData
            {
                entityTypes = entityTypes.ToArray(),
                concepts = concepts.ToArray(),
                relations = relations.ToArray(),
                age = age
            };
            return rootObject;
        }

        private async static Task<TextToAgeResponse> ResolveAgeEntity(string ageText)  {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri(textToAgeEndpoint + "&age=" + ageText)
            })
            {
                HttpResponseMessage response = await client.SendAsync(request);
                if (response.StatusCode == System.Net.HttpStatusCode.OK) {
                    string responseBody = await response?.Content?.ReadAsStringAsync();
                    var ageResult = JsonConvert.DeserializeObject<TextToAgeResponse>(responseBody);
                    return ageResult;
                }
                
            }
            return new TextToAgeResponse();
        }
        #endregion
    }
}