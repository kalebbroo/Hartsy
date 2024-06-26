﻿using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;

namespace Hartsy.Core
{
    public class StableSwarmAPI
    {
        private static readonly HttpClient Client = new();
        private readonly string _swarmURL;
        private static int batchCount = 0;
        private const int batchProcessFrequency = 2;

        public StableSwarmAPI()
        {
            _swarmURL = Environment.GetEnvironmentVariable("SWARM_URL") ?? "";
        }

        /// <summary>Acquires a new session ID from the API.</summary>
        /// <returns>A task that represents the asynchronous operation. The task result contains the session ID.</returns>
        public async Task<string> GetSession()
        {
            try
            {
                JObject sessData = await PostJson($"{_swarmURL}/API/GetNewSession", []);
                string sessionId = sessData["session_id"]!.ToString();
                Console.WriteLine($"Session acquired successfully: {sessionId}");
                return sessionId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetSession: {ex.Message}");
                throw;
            }
        }

        /// <summary>Ensures the WebSocket connection is established.</summary>
        /// <param name="webSocket">The webSocket instance to check and connect if necessary.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task EnsureWebSocketConnectionAsync(ClientWebSocket webSocket)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                Uri serverUri = new($"{_swarmURL.Replace("http", "ws")}/API/GenerateText2ImageWS");
                await webSocket.ConnectAsync(serverUri, CancellationToken.None);
            }
        }

        /// <summary>Sends a request over the WebSocket connection.</summary>
        /// <param name="webSocket">The WebSocket to send the request through.</param>
        /// <param name="request">The request object to be sent.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private static async Task SendRequestAsync(ClientWebSocket webSocket, object request)
        {
            string requestJson = JsonConvert.SerializeObject(request);
            ArraySegment<byte> buffer = new(Encoding.UTF8.GetBytes(requestJson));
            await webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>Receives a message from the WebSocket connection.</summary>
        /// <param name="webSocket">The WebSocket to receive the message from.</param>
        /// <param name="stringBuilder">The StringBuilder to append the received message to.</param>
        /// <param name="responseBuffer">The buffer to store the response bytes.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the WebSocket receive result.</returns>
        private static async Task<WebSocketReceiveResult> ReceiveMessage(ClientWebSocket webSocket,
            StringBuilder stringBuilder, ArraySegment<byte> responseBuffer)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(responseBuffer, CancellationToken.None);
                string jsonStringFragment = Encoding.UTF8.GetString(responseBuffer.Array!, responseBuffer.Offset, result.Count);
                stringBuilder.Append(jsonStringFragment);
            } while (!result.EndOfMessage);
            return result;
        }

        /// <summary>Creates a request object with the necessary payload and session ID.</summary>
        /// <param name="payload">The initial payload for the request.</param>
        /// <returns>A task representing the asynchronous operation. The task result contains the request object as a dictionary.</returns>
        private async Task<Dictionary<string, object>> CreateRequestObject(Dictionary<string, object> payload)
        {
            payload["session_id"] = await GetSession();

            // Remove all entries where the value is null
            List<string> keysToRemove = payload.Where(kvp => kvp.Value == null).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove)
            {
                payload.Remove(key);
            }
            return payload;
        }

        /// <summary>Streams images generated by the API based on the provided payload.</summary>
        /// <param name="payload">The payload containing the parameters for the image generation request.</param>
        /// <param name="username">The username associated with the request.</param>
        /// <param name="messageId">The message ID associated with the request for tracking purposes.</param>
        /// <returns>An async enumerable of tuples, each containing an Image object and a boolean indicating if it is the final image.</returns>
        public async IAsyncEnumerable<(Image<Rgba32>? Image, bool IsFinal)> GetImages(Dictionary<string, object> payload, string username, ulong messageId)
        {
            ClientWebSocket webSocket = new();
            await EnsureWebSocketConnectionAsync(webSocket);
            Dictionary<string, object> request = await CreateRequestObject(payload);
            await SendRequestAsync(webSocket, request);
            ArraySegment<byte> responseBuffer = new(new byte[8192]);
            StringBuilder stringBuilder = new();
            Dictionary<int, Dictionary<string, string>> previewImages = [];
            Dictionary<int, Dictionary<string, string>> finalImages = [];
            while (webSocket.State == WebSocketState.Open)
            {
                stringBuilder.Clear();
                WebSocketReceiveResult result = await ReceiveMessage(webSocket, stringBuilder, responseBuffer);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                string jsonString = stringBuilder.ToString();
                //string logString = ReplaceBase64(jsonString); // DEBUG ONLY
                //Console.WriteLine("Response JSON (excluding base64 data): " + logString); // DEBUG ONLY
                Dictionary<string, object>? responseData = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);
                foreach (KeyValuePair<string, object> kvp in responseData!)
                {
                    if (responseData != null)
                    {
                        if (kvp.Value is JObject genProgressData)
                        {
                            if (genProgressData.ContainsKey("preview"))
                            {
                                bool isFinal = false;
                                int batchIndex = Convert.ToInt32(genProgressData["batch_index"]);
                                string base64WithPrefix = genProgressData["preview"]!.ToString();
                                string overall = genProgressData["overall_percent"]!.ToString();
                                string current = genProgressData["current_percent"]!.ToString();
                                string base64 = await RemovePrefix(base64WithPrefix);
                                previewImages[batchIndex] = new Dictionary<string, string> { { "base64", $"{base64}" } };
                                if (batchIndex == 3)
                                {
                                    batchCount++;
                                    Image<Rgba32>? preview = await HandlePreview(previewImages, batchCount, username, messageId);
                                    if (preview == null)
                                    {
                                        continue;
                                    }
                                    yield return (preview, isFinal);
                                }
                                else
                                {
                                    yield return (null, isFinal);
                                }
                                // TODO: Do we waant to do something with the status data?
                            }
                            else if (responseData.TryGetValue("status", out object? pair) && pair is Dictionary<string, object> statusData)
                            {
                                // List of expected status fields
                                string[] statusFields = ["waiting_gens", "loading_models", "waiting_backends", "live_gens"];
                                foreach (string field in statusFields)
                                {
                                    // Safely get the value of each field, defaulting to 0 if not found
                                    statusData.TryGetValue(field, out object? data);
                                }
                            }
                        }
                        if (responseData.TryGetValue("image", out object? value))
                        {
                            bool isFinal = true;
                            int batchIndex = Convert.ToInt32(responseData["batch_index"]);
                            string base64WithPrefix = value.ToString()!;
                            string base64 = await RemovePrefix(base64WithPrefix);
                            finalImages[batchIndex] = new Dictionary<string, string> { { "base64", $"{base64}" } };
                            if (batchIndex == 3)
                            {
                                Image<Rgba32> final = await HandleFinal(finalImages, username, messageId);
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "All final images received", CancellationToken.None);
                                yield return (final, isFinal);
                                break;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Replaces base64 image data in the JSON string with a placeholder to avoid large log entries.
        /// Additionally, appends the cleaned JSON to a text file.</summary>
        /// <param name="jsonString">The JSON string containing base64 image data.</param>
        /// <returns>A string where base64 image data is replaced with a placeholder and appended to a file.</returns>
        private static string ReplaceBase64(string jsonString)
        {
            // List of base64 image data prefixes to replace with a placeholder
            List<string> prefixes =
                [
                    "\"preview\":\"data:image/jpeg;base64,",
                    "\"image\":\"data:image/jpeg;base64,",
                    "\"preview\":\"data:image/png;base64,",
                    "\"image\":\"data:image/png;base64,",
                    "\"preview\":\"data:image/gif;base64,",
                    "\"image\":\"data:image/gif;base64,",
                    "\"preview\":\"data:image/webp;base64,",
                    "\"image\":\"data:image/webp;base64,"
                ];
            foreach (string prefix in prefixes)
            {
                int start = jsonString.IndexOf(prefix);
                while (start != -1)
                {
                    int end = jsonString.IndexOf('"', start + prefix.Length);
                    if (end != -1)
                    {
                        // Replacing the base64 string with a placeholder
                        jsonString = jsonString.Remove(start, end - start + 1).Insert(start, $"{prefix}[BASE64_DATA]\"");
                        // Move to the next occurrence
                        start = jsonString.IndexOf(prefix, start + prefix.Length + "[BASE64_DATA]\"".Length);
                    }
                    else
                    {
                        // If no closing quote is found, break from the loop
                        break;
                    }
                }
            }
            // Append the modified JSON string to a text file
            File.AppendAllText("json.txt", jsonString + Environment.NewLine);
            return jsonString;
        }


        /// <summary>Processes the preview images for a given batch.</summary>
        /// <param name="previewImages">A dictionary of image data where each key represents a batch index, 
        /// and the value is another dictionary containing image base64.</param>
        /// <param name="batchCount">How many batches have been processed.</param>
        /// <returns>A grid image of the preview images for the batch if count meets frequency; otherwise, null.</returns>
        private static async Task<Image<Rgba32>?> HandlePreview(Dictionary<int, Dictionary<string, string>> previewImages,
            int batchCount, string username, ulong messageId)
        {
            if (batchCount % batchProcessFrequency == 0)
            {
                Image<Rgba32> gridImage = await ImageGrid.CreateGridAsync(previewImages, username, messageId);
                return gridImage;
            }
            return null;
        }

        /// <summary>Processes the final images, generating a grid image from the base64.</summary>
        /// <param name="finalImages">A dictionary where each key represents a batch index, another dictionary containing base64.</param>
        /// <returns>A grid image composed of the final images.</returns>
        private static async Task<Image<Rgba32>> HandleFinal(Dictionary<int, Dictionary<string, string>> finalImages, string username, ulong messageId)
        {
            Image<Rgba32> gridImage = await ImageGrid.CreateGridAsync(finalImages, username, messageId);
            return gridImage;
        }

        /// <summary>Handles the status updates from the WebSocket connection.</summary>
        /// <param name="status">The dictionary containing status information.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
#pragma warning disable IDE0051 // Remove unused private members
        private static async Task HandleStatus(Dictionary<int, Dictionary<string, string>> status)
#pragma warning restore IDE0051 // Remove unused private members
        {
            await Task.Delay(0);
            Console.WriteLine(status);
            Console.WriteLine("Status received");
        }

        /// <summary>Removes the Base64 prefix from a Base64 string if it exists.</summary>
        /// <param name="base64">The Base64 string that may contain a prefix.</param>
        /// <returns>A Base64 string without the prefix.</returns>
        public static Task<string> RemovePrefix(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ArgumentException("Base64 string cannot be null or whitespace.", nameof(base64));
            }
            const string base64Prefix = "base64,";
            int base64StartIndex = base64.IndexOf(base64Prefix);
            if (base64StartIndex != -1)
            {
                base64 = base64[(base64StartIndex + base64Prefix.Length)..];
            }
            return Task.FromResult(base64);
        }

        /// <summary>Sends a JSON post request to the specified URL and returns the JSON response.</summary>
        /// <param name="url">The URL to send the post request to.</param>
        /// <param name="jsonData">The JSON object to send in the post request.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the JSON object response.</returns>
        public static async Task<JObject> PostJson(string url, JObject jsonData)
        {
            try
            {
                StringContent content = new(jsonData.ToString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await Client.PostAsync(url, content);
                string result = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"API Request Failed: {response.StatusCode} - {result}");
                }
                return JObject.Parse(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in PostJson: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Asynchronously creates a GIF using a WebSocket connection based on provided image data and session configurations.
        /// </summary>
        /// <param name="username">The username associated with the request.</param>
        /// <param name="messageId">The message identifier associated with the request.</param>
        /// <param name="payload">The dictionary containing the necessary data and settings for GIF generation.</param>
        /// <returns>A task that represents the asynchronous operation, yielding the generated GIF image.</returns>
        public async IAsyncEnumerable<(string Base64, bool IsFinal, string ETR)> CreateGifAsync(Dictionary<string, object> payload)
        {
            using ClientWebSocket webSocket = new();
            await EnsureWebSocketConnectionAsync(webSocket);
            payload["session_id"] = await GetSession();
            await SendRequestAsync(webSocket, payload);
            ArraySegment<byte> responseBuffer = new(new byte[8192]);
            StringBuilder stringBuilder = new();
            DateTime startTime = DateTime.UtcNow;
            double lastPercent = 0;
            TimeSpan estimatedTimeRemaining = TimeSpan.Zero;
            while (webSocket.State == WebSocketState.Open)
            {
                stringBuilder.Clear();
                WebSocketReceiveResult result = await ReceiveMessage(webSocket, stringBuilder, responseBuffer);
                if (result.MessageType == WebSocketMessageType.Close) break;
                string jsonString = stringBuilder.ToString();
                string logString = ReplaceBase64(jsonString);
                Console.WriteLine("Response JSON (excluding base64 data): " + logString); // DEBUG ONLY
                Dictionary<string, object>? responseData = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);
                bool isFinal = false;
                foreach (KeyValuePair<string, object> kvp in responseData!)
                {
                    if (responseData != null)
                    {
                        if (responseData.TryGetValue("error", out object? isError))
                        {
                            string? error = isError!.ToString();
                            Console.WriteLine($"Error: {error}");
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Error occurred during GIF generation", CancellationToken.None);
                            yield return (string.Empty, false, string.Empty);
                            yield break;
                        }
                        if (kvp.Value is JObject genProgressData)
                        {
                            if (genProgressData.ContainsKey("preview"))
                            {
                                int batchIndex = Convert.ToInt32(genProgressData["batch_index"]);
                                string base64WithPrefix = genProgressData["preview"]!.ToString();
                                string base64 = await RemovePrefix(base64WithPrefix);
                                yield return (base64, isFinal, estimatedTimeRemaining.ToString(@"hh\:mm\:ss"));
                            }
                        }
                        if (responseData.TryGetValue("image", out object? value))
                        {
                            int batchIndex = Convert.ToInt32(responseData["batch_index"]);
                            string base64WithPrefix = value.ToString()!;
                            string base64 = await RemovePrefix(base64WithPrefix);
                            if (base64WithPrefix.Contains("data:image/gif;base64")) isFinal = true;
                            yield return (base64, isFinal, estimatedTimeRemaining.ToString(@"hh\:mm\:ss"));
                        }
                        if (responseData.TryGetValue("gen_progress", out object? progressData))
                        {
                            JObject? progressDict = progressData as JObject;
                            double overallPercent = (double)progressDict["overall_percent"];
                            double currentPercent = (double)progressDict["current_percent"];
                            if (currentPercent == 0.0)
                            {
                                startTime = DateTime.UtcNow;
                                lastPercent = 0;
                                continue;
                            }
                            if (currentPercent > lastPercent)
                            {
                                TimeSpan timeElapsed = DateTime.UtcNow - startTime;
                                double percentComplete = currentPercent;
                                double totalEstimatedTime = timeElapsed.TotalSeconds / percentComplete;
                                totalEstimatedTime = Math.Min(totalEstimatedTime, 24 * 60 * 60);
                                estimatedTimeRemaining = TimeSpan.FromSeconds((1 - percentComplete) * totalEstimatedTime);
                                lastPercent = currentPercent;
                            }
                        }
                        if (isFinal)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "All final images received", CancellationToken.None);
                            break;
                        }
                    }
                }
            }
        }
    }
}