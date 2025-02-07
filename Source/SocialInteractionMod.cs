using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using HarmonyLib;
using RestSharp;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace SocialInteractionLLM
{
    public class OllamaSettings : ModSettings
    {
        public string modelName = "llama3.2:3b";
        public string endpoint = "http://localhost:11434";
        public bool enableMod = true;
        public float temperature = 0.7f;
        public string systemPrompt = "You are generating social interaction messages between two RimWorld colonists. Keep responses immersive, brief, and fitting the game's tone. Responses should be a single sentence within quotes. Make it more detailed and personality-driven, considering their traits and relationship.";
    }

    public class SocialInteractionMod : Mod
    {
        public static OllamaSettings settings;

        public SocialInteractionMod(ModContentPack content) : base(content)
        {
            settings = new OllamaSettings();
            var harmony = new Harmony("com.example.socialinteractionllm");
            harmony.PatchAll();
        }

        public override string SettingsCategory()
        {
            return "Social Interaction LLM";
        }
    }

    [HarmonyPatch(typeof(PlayLog), nameof(PlayLog.Add))]
    public static class Verse_PlayLog_Add_Patch
    {
        private static RestClient restClient = new RestClient(SocialInteractionMod.settings.endpoint);

        private static HashSet<LogEntry> processedEntries = new HashSet<LogEntry>();

        private static void Postfix(LogEntry entry)
        {
            if (!SocialInteractionMod.settings.enableMod) return;

            // Safeguard: Prevent recursion by checking if this entry has already been processed
            if (processedEntries.Contains(entry)) return;

            try
            {
                // Process only entries of type PlayLogEntry_Interaction
                if (!(entry is PlayLogEntry_Interaction interaction)) return;

                // Use reflection to get initiator and recipient
                var initiator = AccessTools.Field(typeof(PlayLogEntry_Interaction), "initiator").GetValue(interaction) as Pawn;
                var recipient = AccessTools.Field(typeof(PlayLogEntry_Interaction), "recipient").GetValue(interaction) as Pawn;

                if (initiator == null || recipient == null) return;

                Regex RemoveColorTag = new Regex("<\\/?color[^>]*>");
                string txt = interaction.ToGameStringFromPOV(initiator);
                string interactionText = RemoveColorTag.Replace(txt, string.Empty);

                // Generate LLM prompt from existing interaction details
                string prompt = GeneratePrompt(initiator, recipient, interactionText);

                // Run the LLM call on a background thread
                Task.Run(async () =>
                {
                    string llmOutput = await CallOllamaAsync(prompt);

                    if (!string.IsNullOrEmpty(llmOutput))
                    {
                        // Add a new message entry to the PlayLog without modifying the original one
                        AddCustomLogEntry(initiator, recipient, llmOutput);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error($"Postfix: Error processing PlayLogEntry -> {e.Message}\n{e.StackTrace}");
            }
        }

        private static string GeneratePrompt(Pawn initiator, Pawn recipient, string originalMessage)
        {
            float opinion = initiator.relations.OpinionOf(recipient);
            return $@"Initiator: {initiator.Name.ToStringShort} (Traits: {string.Join(", ", initiator.story.traits.allTraits)})
                     Recipient: {recipient.Name.ToStringShort} (Traits: {string.Join(", ", recipient.story.traits.allTraits)})
                     Their relationship: {(opinion > 0 ? "Positive" : "Negative")} ({opinion} opinion)
                     Original message: {originalMessage}";
			Log.Message($"{originalMessage}");
        }

        private static async Task<string> CallOllamaAsync(string prompt)
        {
            try
            {
                var request = new RestRequest("/api/generate", Method.POST)
                              .AddJsonBody(new
                              {
                                  model = SocialInteractionMod.settings.modelName,
                                  prompt = EscapeJsonString(prompt),
                                  temperature = SocialInteractionMod.settings.temperature.ToString("F1"),
                                  stream = false,
                                  system = SocialInteractionMod.settings.systemPrompt
                              });

                var response = await restClient.ExecuteAsync(request);

                if (response.IsSuccessful)
                {
                    // Log.Message($"Received API response: {response.Content}");

                    string message = ExtractResponseFromJson(response.Content.ToString());
                    message = StripColorCodes(message);
                    Log.Message($"{message}");

                    return message;
                }

                Log.Error($"Ollama API error: {response.StatusCode} - {response.ErrorMessage}");
                return null;
            }
            catch (Exception e)
            {
                Log.Error($"Error calling Ollama: {e.GetType().Name} - {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        private static string ExtractResponseFromJson(string jsonResponse)
        {
            try
            {
                int startIndex = jsonResponse.IndexOf("\"response\":\"") + "\"response\":\"".Length;
                int endIndex = jsonResponse.IndexOf("\",\"done\"", startIndex);

                if (startIndex >= 0 && endIndex > startIndex)
                {
                    string extracted = jsonResponse.Substring(startIndex, endIndex - startIndex);
                    string unescaped = Regex.Unescape(extracted);
                    return unescaped;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error extracting response from JSON: {ex.Message}");
            }

            return null;
        }

        private static string StripColorCodes(string input)
        {
            string decoded = input.Replace("\\u003c", "<").Replace("\\u003e", ">");
            var htmlColorTagPattern = @"<color=#\w+>|</color>";
            return Regex.Replace(decoded, htmlColorTagPattern, string.Empty);
        }

        private static string EscapeJsonString(string str)
        {
            return str.Replace("\"", "\\\"")
                     .Replace("\r", "\\r")
                     .Replace("\n", "\\n")
                     .Replace("\t", "\\t");
        }

        private static void AddCustomLogEntry(Pawn initiator, Pawn recipient, string message)
{
    try
    {
        if (initiator == null || recipient == null)
        {
            Log.Error("AddCustomLogEntry: Initiator or recipient is null. Cannot create custom log entry.");
            return;
        }

        // Combine the message for display
        //said to {recipient.Name.ToStringShort}
        var combinedMessage = $"{initiator.Name.ToStringShort}: {message}";

        // Display the message to the player using RimWorld's in-game message system
        Messages.Message(combinedMessage, MessageTypeDefOf.NeutralEvent, historical: false);

        Log.Message($"Generated LLM Interaction Message: {combinedMessage}");
    }
    catch (Exception ex)
    {
        Log.Error($"Error while adding a new custom log entry: {ex.Message}\n{ex.StackTrace}");
    }
}
    }
}
