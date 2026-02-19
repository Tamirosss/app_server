using Microsoft.EntityFrameworkCore;
using Google.GenAI;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors();
builder.Services.AddDbContext<WorkoutDbContext>(options =>
    options.UseSqlite("Data Source=workout.db"));
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkoutDbContext>();
    db.Database.EnsureCreated();
}
app.MapGet("/", () => "API is running");

app.UseCors(policy =>
{
    policy.AllowAnyOrigin()
          .AllowAnyMethod()
          .AllowAnyHeader();
});

var apiKey = "AIzaSyC8svFb2l7jAUII8VdIn8KjJ2d390aIH9E";

app.MapPost("/register", async (UserCredentials credentials, WorkoutDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(credentials.username) || string.IsNullOrWhiteSpace(credentials.password))
    {
        return Results.Json(new { success = false, message = "Username and password are required" });
    }

    if (credentials.username.Length < 3)
    {
        return Results.Json(new { success = false, message = "Username must be at least 3 characters" });
    }

    if (credentials.password.Length < 6)
    {
        return Results.Json(new { success = false, message = "Password must be at least 6 characters" });
    }

    var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Username == credentials.username);
    if (existingUser != null)
    {
        return Results.Json(new { success = false, message = "Username already exists" });
    }

    var newUser = new User
    {
        Username = credentials.username,
        Password = credentials.password
    };

    db.Users.Add(newUser);
    await db.SaveChangesAsync();

    Console.WriteLine($"[REGISTER] New user created: {newUser.Username} with ID: {newUser.Id}");

    return Results.Json(new { success = true, message = "User registered successfully", username = newUser.Username, userId = newUser.Id });
});

app.MapPost("/login", async (UserCredentials credentials, WorkoutDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(credentials.username) || string.IsNullOrWhiteSpace(credentials.password))
    {
        return Results.Json(new { success = false, message = "Username and password are required" });
    }

    var user = await db.Users.FirstOrDefaultAsync(u =>
        u.Username == credentials.username &&
        u.Password == credentials.password);

    if (user == null)
    {
        return Results.Json(new { success = false, message = "Invalid username or password" });
    }

    Console.WriteLine($"[LOGIN] User logged in: {user.Username} with ID: {user.Id}");

    return Results.Json(new { success = true, message = "Login successful", username = user.Username, userId = user.Id });
});

app.MapGet("/get-user-workout", async (int userId, WorkoutDbContext db) =>
{
    Console.WriteLine($"[GET-WORKOUT] Request for userId: {userId}");

    try
    {
        var workoutPlans = await db.WorkoutPlans
            .Include(wp => wp.Exercises)
            .Where(wp => wp.UserId == userId)
            .OrderBy(wp => wp.DayNumber)
            .ToListAsync();

        if (workoutPlans == null || workoutPlans.Count == 0)
        {
            Console.WriteLine($"[GET-WORKOUT] No workouts found for userId: {userId}");
            return Results.Json(new List<object>());
        }

        var workouts = workoutPlans.Select(wp => new
        {
            name = wp.Name,
            excercises = wp.Exercises.OrderBy(e => e.OrderIndex).Select(e => new
            {
                name = e.Name,
                sets = e.Sets,
                reps = e.Reps,
                restTime = e.RestTime,
                videoLink = e.VideoLink
            }).ToList()
        }).ToList();

        Console.WriteLine($"[GET-WORKOUT] Found {workouts.Count} workouts for userId: {userId}");

        return Results.Json(workouts);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GET-WORKOUT] ERROR: {ex.Message}");
        return Results.Problem($"Error: {ex.Message}");
    }
});

app.MapGet("/workouts", async (int userId, int age, string gender, string history, string goal, string location, int weight, int height, int amount, WorkoutDbContext db) =>
{
    Console.WriteLine($"[WORKOUTS] Request received - UserId: {userId}, Age: {age}, Gender: {gender}, Goal: {goal}");

    var client = new Client(apiKey: apiKey);

    var contents = $@"i have the following json structure:
{{
    ""name"": ""workout day name"",
    ""excercises"": [
        {{
            ""name"": ""exercise name"",
            ""sets"": 3,
            ""reps"": 12,
            ""restTime"": 60,
            ""videoLink"": ""exercise name""
        }}
    ]
}}

build me a workout for someone with these stats:
gender: {gender}
height: {height}cm, weight: {weight}kg, age: {age}
goal: {goal}
workout history: {history} 
goal workouts per week: {amount}
workout location: {location}

Return ONLY a JSON array of {amount} workout objects (one for each day).
Each workout MUST have a 'name' field (like 'Push Day', 'Pull Day', etc.) and an 'excercises' array (note: excercises with TWO e's).

CRITICAL: For videoLink - put ONLY the exercise name as plain text. DO NOT include any URLs or links.
Example: ""videoLink"": ""bench press"" NOT ""videoLink"": ""https://...""

Take into account the person's gender when selecting exercises and intensity levels.

DO NOT RETURN ANY OTHER TEXT EXCEPT THE JSON ARRAY.";

    try
    {
        Console.WriteLine("[WORKOUTS] Sending request to Gemini...");

        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: contents
        );

        var resultText = response.Candidates[0].Content.Parts[0].Text;
        var cleanJson = CleanJsonString(resultText);

        Console.WriteLine($"[WORKOUTS] Received response from Gemini: {cleanJson.Substring(0, Math.Min(200, cleanJson.Length))}...");

        var workoutsData = JsonSerializer.Deserialize<List<WorkoutData>>(cleanJson);

        if (workoutsData != null && workoutsData.Count > 0)
        {
            Console.WriteLine($"[WORKOUTS] Parsed {workoutsData.Count} workouts. Saving to database...");

            var oldPlans = db.WorkoutPlans.Where(wp => wp.UserId == userId);
            var oldExercises = db.Exercises.Where(e => oldPlans.Select(wp => wp.Id).Contains(e.WorkoutPlanId));
            db.Exercises.RemoveRange(oldExercises);
            db.WorkoutPlans.RemoveRange(oldPlans);
            await db.SaveChangesAsync();

            for (int i = 0; i < workoutsData.Count; i++)
            {
                var workoutPlan = new WorkoutPlan
                {
                    UserId = userId,
                    Name = workoutsData[i].name ?? $"Day {i + 1}",
                    DayNumber = i + 1
                };

                db.WorkoutPlans.Add(workoutPlan);
                await db.SaveChangesAsync();

                if (workoutsData[i].excercises != null)
                {
                    Console.WriteLine($"[WORKOUTS] Saving {workoutsData[i].excercises.Count} exercises for workout {i + 1}");

                    for (int j = 0; j < workoutsData[i].excercises.Count; j++)
                    {
                        var ex = workoutsData[i].excercises[j];
                        var exercise = new Exercise
                        {
                            WorkoutPlanId = workoutPlan.Id,
                            Name = ex.name ?? "Unknown Exercise",
                            Sets = ex.sets,
                            Reps = ex.reps,
                            RestTime = ex.restTime,
                            VideoLink = ex.videoLink ?? ex.name ?? "",
                            OrderIndex = j
                        };
                        db.Exercises.Add(exercise);
                    }
                }
            }

            await db.SaveChangesAsync();
            Console.WriteLine("[WORKOUTS] Successfully saved all workouts to database!");
        }
        else
        {
            Console.WriteLine("[WORKOUTS] WARNING: No workouts were parsed from Gemini response!");
        }

        return Results.Content(cleanJson, "application/json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WORKOUTS] ERROR: {ex.Message}");
        Console.WriteLine($"[WORKOUTS] Stack trace: {ex.StackTrace}");
        return Results.Problem($"Error: {ex.Message}");
    }
});

app.MapGet("/replace-exercise", async (string exerciseName) =>
{
    Console.WriteLine($"[REPLACE] Request to replace exercise: {exerciseName}");

    var client = new Client(apiKey: apiKey);

    var contents = $@"Find an alternative exercise for: {exerciseName}

Return ONLY a JSON object in this exact format (no other text):
{{
    ""name"": ""exercise name"",
    ""sets"": 3,
    ""reps"": 12,
    ""restTime"": 60,
    ""videoLink"": ""exercise name""
}}

CRITICAL: For videoLink - put ONLY the exercise name as plain text. DO NOT include any URLs or links.
Example: ""videoLink"": ""dumbbell press"" NOT ""videoLink"": ""https://...""

DO NOT return any text except the JSON object.";

    try
    {
        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: contents
        );

        var resultText = response.Candidates[0].Content.Parts[0].Text;
        var cleanJson = CleanJsonString(resultText);

        Console.WriteLine($"[REPLACE] Alternative exercise found: {cleanJson}");

        return Results.Content(cleanJson, "application/json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[REPLACE] ERROR: {ex.Message}");
        return Results.Problem($"Error: {ex.Message}");
    }
});



app.Run();

string CleanJsonString(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return input;

    return input
        .Replace("```json", "")
        .Replace("```", "")
        .Trim();
}

// ============== DATABASE MODELS ==============

public class WorkoutDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<WorkoutPlan> WorkoutPlans { get; set; }
    public DbSet<Exercise> Exercises { get; set; }
    public DbSet<WorkoutProgress> WorkoutProgress { get; set; }

    public WorkoutDbContext(DbContextOptions<WorkoutDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkoutPlan>()
            .HasOne(wp => wp.User)
            .WithMany(u => u.WorkoutPlans)
            .HasForeignKey(wp => wp.UserId);

        modelBuilder.Entity<Exercise>()
            .HasOne(e => e.WorkoutPlan)
            .WithMany(wp => wp.Exercises)
            .HasForeignKey(e => e.WorkoutPlanId);

        modelBuilder.Entity<WorkoutProgress>()
            .HasOne(wp => wp.User)
            .WithMany(u => u.Progress)
            .HasForeignKey(wp => wp.UserId);
    }
}

public class User
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Username { get; set; }

    [Required]
    public string Password { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<WorkoutPlan> WorkoutPlans { get; set; } = new();
    public List<WorkoutProgress> Progress { get; set; } = new();
}

public class WorkoutPlan
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [Required]
    public string Name { get; set; }

    public int DayNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; }
    public List<Exercise> Exercises { get; set; } = new();
}

public class Exercise
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int WorkoutPlanId { get; set; }

    [Required]
    public string Name { get; set; }

    public int Sets { get; set; }

    public int Reps { get; set; }

    public int RestTime { get; set; }

    public string VideoLink { get; set; }

    public int OrderIndex { get; set; }

    public WorkoutPlan WorkoutPlan { get; set; }
}

public class WorkoutProgress
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [Required]
    public string ExerciseName { get; set; }

    public int Sets { get; set; }

    public int Reps { get; set; }

    public double Weight { get; set; }

    public string Notes { get; set; }

    public DateTime CompletedAt { get; set; }

    public User User { get; set; }
}

// ============== DTOs ==============

public record UserCredentials(string username, string password);

public class WorkoutData
{
    public string name { get; set; }
    public List<ExerciseData> excercises { get; set; }
}

public class ExerciseData
{
    public string name { get; set; }
    public int sets { get; set; }
    public int reps { get; set; }
    public int restTime { get; set; }
    public string videoLink { get; set; }
}

public class WorkoutProgressDto
{
    public int userId { get; set; }
    public string exerciseName { get; set; }
    public int sets { get; set; }
    public int reps { get; set; }
    public double weight { get; set; }
    public string notes { get; set; }
}










