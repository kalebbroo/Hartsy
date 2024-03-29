﻿using Postgrest.Attributes;
using Postgrest.Models;
using Supabase;
using static Postgrest.Constants;
using System.Text.Json.Serialization;
using System.Text.Json;
using dotenv.net;

public class SupabaseClient
{
    private Supabase.Client supabase;

    public SupabaseClient()
    {
        InitializeSupabase().GetAwaiter().GetResult();
    }

    private async Task InitializeSupabase()
    {
        var url = Environment.GetEnvironmentVariable("SUPABASE_URL");
        var key = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_KEY");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
            var envFilePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../../.env"));
            Console.WriteLine("Attempting to load .env file from: " + envFilePath);
            if (File.Exists(envFilePath))
            {
                var envOptions = new DotEnvOptions(envFilePaths: new[] { envFilePath });
                DotEnv.Load(envOptions);
                url = Environment.GetEnvironmentVariable("SUPABASE_URL");
                key = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_KEY");

            }
        }
        var options = new SupabaseOptions
        {
            AutoConnectRealtime = true
        };
        supabase = new Supabase.Client(url, key, options);
        await supabase.InitializeAsync();
    }

    public async Task<bool> IsDiscordLinked(string discordId)
    {
        try
        {
            // Check if any user has the provided Discord ID
            var result = await supabase
                .From<Users>()
                .Select("*") // Selects all fields; replace '*' with specific fields as needed
                .Filter("provider_id", Operator.Equals, discordId)
                .Get();

            // If the response contains any users, it means the user's Discord ID is linked
            bool isLinked = result.Models?.Count > 0;

            return isLinked;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception when checking Discord link: {ex.Message}");
            return false; // Return false in case of error
        }
    }

    public async Task<Users?> GetUserByDiscordId(string discordId)
    {
        try
        {
            var response = await supabase
                .From<Users>()
                .Select("*")
                .Filter("provider_id", Operator.Equals, discordId)
                .Single();

            if (response == null)
            {
                Console.WriteLine($"No user found with Discord ID: {discordId}");
                return null;
            }

            return response;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching user by Discord ID {discordId}: {ex.Message}");
            return null;
        }
    }


    public async Task<Dictionary<string, object>?> GetSubStatus(string discordId)
    {
        try
        {
            var user = await GetUserByDiscordId(discordId);
            if (user == null)
            {
                Console.WriteLine("User not found.");
                return null;
            }

            var subStatus = new Dictionary<string, object>
        {
            {"PlanName", user.PlanName ?? "No plan"},
            {"Credits", user.Credit ?? 0},
            // Add any other subscription info here
        };

            return subStatus;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching subscription status: {ex.Message}");
            return null;
        }
    }

    public async Task<Users?> GetSubscriptionByUserId(string userId)
    {
        try
        {
            Console.WriteLine("\nAttempting to fetch subscription for user ID: " + userId + "\n");

            // Query to get subscription data for a specific user ID
            var response = await supabase.From<Users>()
            .Select("*")
            .Filter("id", Operator.Equals, userId)
            .Single();

            if (response != null)
            {
                return response;
            }
            else
            {
                Console.WriteLine("No subscription found for user ID: " + userId);
                return null; // Return null if no subscription is found
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while fetching subscription for user ID {userId}: {ex.Message}");
            return null; // Return null in case of error
        }
    }

    public async Task<Dictionary<string, Template>?> GetTemplatesAsync()
    {
        try
        {
            var response = await supabase.From<Template>().Select("*").Get();

            // Ensure we have models to work with
            if (response.Models != null && response.Models.Any())
            {
                // Convert the list of templates to a dictionary
                var templatesDictionary = response.Models.ToDictionary(template => template.Name, template => template);
                return templatesDictionary;
            }
            else
            {
                Console.WriteLine("No templates found.");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetTemplates: {ex.Message}");
            return null;
        }
    }

    public async Task AddGenerationAsync(string discordId)
    {
        try
        { 
            var url = Environment.GetEnvironmentVariable("RUNPOD_URL");
            var user = await GetUserByDiscordId(discordId);
            if (user == null)
            {
                Console.WriteLine("User not found.");
            }
            var userId = user.Id;
            var newGeneration = new Generations
            {
                Id = 1000,
                UserId = userId,
                Batch = 1,
                //Duration = 1000,
                Positive = "Example of positive prompt",
                Negative = "Example of negative aspects",
                Checkpoint = "StarlightXL.safetensors",
                CreatedAt = DateTime.UtcNow,
                ComfyEndpoint = url,
                ComfyPromptId = "e639da24-6ce1-46df-93b0-c5f20fe79c3b",
                Width = 1024,
                Height = 768,
                //TemplateId = 123,
                Status = "queued"
            };

            var response = await supabase.From<Generations>().Insert(newGeneration);

            if (response == null)
            {
                Console.WriteLine($"Error inserting new generation: {response.ResponseMessage}");
            }
            else
            {
                Console.WriteLine("New generation row added successfully.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding generation: {ex.Message}");
        }
    }

    public async Task AddTemplate(Template newTemplate)
    {
        try
        {

            var response = await supabase.From<Template>().Insert(newTemplate);

            if (response == null)
            {
                Console.WriteLine($"Error inserting new template: {response.ResponseMessage}");
            }
            else
            {
                Console.WriteLine("New template row added successfully.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding template: {ex.Message}");
        }
    }

    public async Task<bool> UpdateUserCredit(string userId, int newCredit)
    {
        try
        {
            var response = await supabase.From<Users>()
                                         .Where(x => x.ProviderId == userId)
                                         .Set(x => x.Credit, newCredit)
                                         .Update();
            // Check the result of the operation
            Console.WriteLine(response.ResponseMessage.IsSuccessStatusCode);
            if (response.ResponseMessage.IsSuccessStatusCode)
            {
                Console.WriteLine("User credit updated successfully.");
                return true;
            }
            else
            {
                Console.WriteLine("Error updating user credit.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating user credit: {ex.Message}");
            return false;
        }
    }


    [Table("users")]
    public class Users : BaseModel
    {
        [PrimaryKey("id", false)]
        public string? Id { get; set; }

        [Column("full_name")]
        public string? Name { get; set; }

        [Column("avatar_url")]
        public string? Avatar_URL { get; set; }

        [Column("billing_address")]
        public string? Billing { get; set; }

        [Column("payment_method")]
        public string? Payment { get; set; }

        [Column("email")]
        public string? Email { get; set; }

        [Column("username")]
        public string? Username { get; set; }

        [Column("likes_count")]
        public int? Likes { get; set; }

        [Column("created_at")]
        public string? Created { get; set; }

        [Column("provider")]
        public string? Provider { get; set; }

        [Column("provider_id")]
        public string? ProviderId { get; set; }

        [Column("credit_limit")]   
        public int? Credit { get; set; }

        [Column("banner_url")]
        public string? Banner { get; set; }

        [Column("stripe_customer_id")]
        public string? StripeId { get; set; }

        [Column("gallery_size")]
        public int? Gallery { get; set; }

        [Column("plan_name")]
        public string? PlanName { get; set; }


    }

    [Table("subscriptions")]
    public class Subscriptions : BaseModel
    {
        [PrimaryKey("id", false)]
        public string? Id { get; set; }

        [Column("user_id")]
        public string? UserId { get; set; }

        [Column("status")]
        public string? Status { get; set; }

        [Column("metadata")]
        public string? MetadataJson { get; set; } // Keeping as string but changing the name for clarity

        // Not stored in DB, just a convenient way to access the parsed metadata
        [JsonIgnore] // Make sure this isn't attempted to be mapped by your ORM
        public Dictionary<string, object>? Metadata
        {
            get
            {
                if (string.IsNullOrEmpty(MetadataJson)) return null;
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, object>>(MetadataJson);
                }
                catch (JsonException)
                {
                    Console.WriteLine("Failed to parse JSON from metadata.");
                    return null;
                }
            }
        }

        [Column("price_id")]
        public string? PriceId { get; set; }

        [Column("quantity")]
        public int? Quantity { get; set; }

        [Column("cancel_at_period_end")]
        public bool? CancelAtPeriodEnd { get; set; }

        [Column("created")]
        public DateTime? Created { get; set; }

        [Column("current_period_start")]
        public DateTime? CurrentPeriodStart { get; set; }

        [Column("current_period_end")]
        public DateTime? CurrentPeriodEnd { get; set; }

        [Column("ended_at")]
        public DateTime? EndedAt { get; set; }

        [Column("cancel_at")]
        public DateTime? CancelAt { get; set; }

        [Column("canceled_at")]
        public DateTime? CanceledAt { get; set; }

        [Column("trial_start")]
        public DateTime? TrialStart { get; set; }

        [Column("trial_end")]
        public DateTime? TrialEnd { get; set; }

        [Column("amount")]
        public int? Amount { get; set; }
    }


    [Table("templates")]
    public class Template : BaseModel
    {
        [PrimaryKey("id", false)]
        public long? Id { get; set; }

        [Column("prompt")]
        public string? Prompt { get; set; }

        [Column("positive")]
        public string? Positive { get; set; }

        [Column("negative")]
        public string? Negative { get; set; }

        [Column("checkpoint")]
        public string? Checkpoint { get; set; }

        [Column("seed")]
        public long? Seed { get; set; }

        [Column("created_at")]
        public string? CreatedAt { get; set; }

        [Column("order_rank")]
        public long? OrderRank { get; set; }

        [Column("active")]
        public bool? Active { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        [Column("image_url")]
        public string? ImageUrl { get; set; }

        [Column("user_id")]
        public string? UserId { get; set; }
    }

    [Table("prices")]
    public class Prices : BaseModel
    {
        [PrimaryKey("id", false)]
        public string? Id { get; set; }

        [Column("product_id")]
        public string? ProductId { get; set; }

        [Column("active")]
        public bool Active { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        [Column("unit_amount")]
        public long UnitAmount { get; set; }

        [Column("currency")]
        public string? Currency { get; set; }

        [Column("type")]
        public string? Type { get; set; }

        [Column("interval")]
        public string? Interval { get; set; }

        //[Column("interval_count")]
        //public int IntervalCount { get; set; }

        [Column("trial_period_days")]
        public int? TrialPeriodDays { get; set; }

        //[Column("metadata")]
        //public string? Metadata { get; set; }

        [Column("is_default")]
        public bool IsDefault { get; set; }

        [Column("is_metered")]
        public bool IsMetered { get; set; }

        [Column("is_topup")]
        public bool IsTopup { get; set; }
    }

    [Table("images")]
    public class Images : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("generation_id")]
        public long GenerationId { get; set; }

        [Column("image_url")]
        public string ImageUrl { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("likes_count")]
        public long LikesCount { get; set; }

        //[Column("template_id")]
        //public long TemplateId { get; set; }

        [Column("is_public")]
        public bool IsPublic { get; set; }
    }

    [Table("generations")]
    public class Generations : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("user_id")]
        public string? UserId { get; set; }

        [Column("batch")]
        public short Batch { get; set; }

        //[Column("duration")]
        //public long Duration { get; set; }

        [Column("positive")]
        public string? Positive { get; set; }

        [Column("negative")]
        public string? Negative { get; set; }

        [Column("checkpoint")]
        public string? Checkpoint { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("comfy_endpoint")]
        public string? ComfyEndpoint { get; set; }

        [Column("comfy_prompt_id")]
        public string? ComfyPromptId { get; set; }

        [Column("width")]
        public long Width { get; set; }

        [Column("height")]
        public long Height { get; set; }

        //[Column("template_id")]
        //public long TemplateId { get; set; }

        [Column("status")]
        public string? Status { get; set; }
    }

}
