using Microsoft.EntityFrameworkCore;
using Google.GenAI;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

// ── יצירת ה-builder של האפליקציה (קורא הגדרות, args, וכו') ──────────────────
var builder = WebApplication.CreateBuilder(args);

// מאפשר שירות CORS (גישה משרתים/דומיינים אחרים)
builder.Services.AddCors();

// ── Database: PostgreSQL בפרודקשן, SQLite בדב ──────────────────────────────
// מושכים את כתובת הדאטהבייס ממשתנה סביבה. אם הוא קיים = פרודקשן.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

if (!string.IsNullOrEmpty(databaseUrl))
{
    // פרודקשן: משתמשים ב-PostgreSQL
    Console.WriteLine("[DB] Using PostgreSQL from DATABASE_URL");

    // מפרקים את ה-URL לרכיבים (host, port, user, password וכו')
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':'); // מפריד שם משתמש מסיסמה

    // בונים מחרוזת חיבור (connection string) בפורמט של Npgsql
    var connectionString =
        $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')}" +
        $";Username={userInfo[0]};Password={userInfo[1]};Pooling=true;";

    // רושמים את ה-DbContext עם PostgreSQL
    builder.Services.AddDbContext<WorkoutDbContext>(options =>
        options.UseNpgsql(connectionString));
}
else
{
    // פיתוח מקומי: משתמשים בקובץ SQLite פשוט
    Console.WriteLine("[DB] Using SQLite for local development");
    builder.Services.AddDbContext<WorkoutDbContext>(options =>
        options.UseSqlite("Data Source=workout.db"));
}

// בונים את האפליקציה בפועל
var app = builder.Build();

// ── יצירת טבלאות אם לא קיימות ────────────────────────────────────────────
// פותחים scope זמני כדי לגשת לדאטהבייס בזמן האתחול
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkoutDbContext>();
    try
    {
        // יוצר את כל הטבלאות לפי המודלים אם הן עוד לא קיימות
        Console.WriteLine("[DB] Running EnsureCreated...");
        db.Database.EnsureCreated();
        Console.WriteLine("[DB] Done.");
    }
    catch (Exception ex)
    {
        // אם נכשל - מדפיס שגיאה וזורק אותה הלאה (האפליקציה לא תעלה)
        Console.WriteLine($"[DB] ERROR: {ex.Message}");
        throw;
    }
}

// ── הגדרת CORS: מאפשר גישה מכל מקור, כל מתודה וכל header ──────────────────
app.UseCors(policy =>
{
    policy.AllowAnyOrigin()
          .AllowAnyMethod()
          .AllowAnyHeader();
});

// ── API Key של Gemini מ-env variable ─────────────────────────────────────
// מושכים את המפתח של Gemini. אם חסר - זורקים שגיאה ברורה בזמן האתחול.
var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
             ?? throw new InvalidOperationException("GEMINI_API_KEY environment variable is not set.");

// ============================================================
// POST /register  ─ רישום משתמש חדש
// ============================================================
app.MapPost("/register", async (UserCredentials credentials, WorkoutDbContext db) =>
{
    // ולידציה: שם משתמש וסיסמה חייבים להיות מלאים
    if (string.IsNullOrWhiteSpace(credentials.username) || string.IsNullOrWhiteSpace(credentials.password))
        return Results.Json(new { success = false, message = "Username and password are required" });

    // ולידציה: שם משתמש לפחות 3 תווים
    if (credentials.username.Length < 3)
        return Results.Json(new { success = false, message = "Username must be at least 3 characters" });

    // ולידציה: סיסמה לפחות 6 תווים
    if (credentials.password.Length < 6)
        return Results.Json(new { success = false, message = "Password must be at least 6 characters" });

    // בדיקה אם שם המשתמש כבר קיים בדאטהבייס
    var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Username == credentials.username);
    if (existingUser != null)
        return Results.Json(new { success = false, message = "Username already exists" });

    // יצירת אובייקט משתמש חדש
    // ⚠️ הערה: הסיסמה נשמרת כטקסט גלוי (plain text) - לא מאובטח!
    //    בפרודקשן צריך להצפין/לעשות hash לסיסמה (למשל BCrypt).
    var newUser = new User
    {
        Username = credentials.username,
        Password = credentials.password
    };

    // הוספה ושמירה בדאטהבייס
    db.Users.Add(newUser);
    await db.SaveChangesAsync();

    Console.WriteLine($"[REGISTER] New user created: {newUser.Username} with ID: {newUser.Id}");

    // החזרת תשובת הצלחה עם הפרטים
    return Results.Json(new { success = true, message = "User registered successfully", username = newUser.Username, userId = newUser.Id });
});

// ============================================================
// POST /login  ─ התחברות משתמש קיים
// ============================================================
app.MapPost("/login", async (UserCredentials credentials, WorkoutDbContext db) =>
{
    // ולידציה: שם וסיסמה חייבים להיות מלאים
    if (string.IsNullOrWhiteSpace(credentials.username) || string.IsNullOrWhiteSpace(credentials.password))
        return Results.Json(new { success = false, message = "Username and password are required" });

    // חיפוש משתמש שמתאים גם לשם וגם לסיסמה
    var user = await db.Users.FirstOrDefaultAsync(u =>
        u.Username == credentials.username &&
        u.Password == credentials.password);

    // אם לא נמצא - שם משתמש או סיסמה שגויים
    if (user == null)
        return Results.Json(new { success = false, message = "Invalid username or password" });

    Console.WriteLine($"[LOGIN] User logged in: {user.Username} with ID: {user.Id}");

    // החזרת הצלחה עם פרטי המשתמש
    return Results.Json(new { success = true, message = "Login successful", username = user.Username, userId = user.Id });
});

// ============================================================
// GET /get-user-workout  ─ שליפת תוכנית האימון השמורה של המשתמש
// ============================================================
app.MapGet("/get-user-workout", async (int userId, WorkoutDbContext db) =>
{
    Console.WriteLine($"[GET-WORKOUT] Request for userId: {userId}");

    try
    {
        // שולפים את כל תוכניות האימון של המשתמש,
        // כולל התרגילים שלהן (Include), ממוינות לפי מספר היום
        var workoutPlans = await db.WorkoutPlans
            .Include(wp => wp.Exercises)
            .Where(wp => wp.UserId == userId)
            .OrderBy(wp => wp.DayNumber)
            .ToListAsync();

        // אם אין אימונים - מחזירים רשימה ריקה
        if (workoutPlans == null || workoutPlans.Count == 0)
        {
            Console.WriteLine($"[GET-WORKOUT] No workouts found for userId: {userId}");
            return Results.Json(new List<object>());
        }

        // ממירים את הנתונים מהדאטהבייס למבנה ה-JSON שהלקוח מצפה לו
        var workouts = workoutPlans.Select(wp => new
        {
            name = wp.Name,
            // התרגילים ממוינים לפי OrderIndex (סדר התצוגה)
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
        // טיפול בשגיאה - לוג והחזרת תשובת שגיאה
        Console.WriteLine($"[GET-WORKOUT] ERROR: {ex.Message}");
        return Results.Problem($"Error: {ex.Message}");
    }
});

// ============================================================
// GET /workouts  ─ יצירת תוכנית אימון חדשה ע"י Gemini ושמירתה
// ============================================================
app.MapGet("/workouts", async (int userId, int age, string history, string goal, string location, int weight, int height, int amount, WorkoutDbContext db) =>
{
    Console.WriteLine($"[WORKOUTS] Request received - UserId: {userId}, Age: {age}, Goal: {goal}");

    // יוצרים client של Gemini עם המפתח
    var client = new Client(apiKey: apiKey);

    // בונים את ה-prompt ל-Gemini: מגדירים את מבנה ה-JSON הרצוי
    // ומכניסים את כל הנתונים האישיים של המשתמש
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
height: {height}cm, weight: {weight}kg, age: {age}
goal: {goal}
workout history: {history} 
goal workouts per week: {amount}
workout location: {location}

Return ONLY a JSON array of {amount} workout objects (one for each day).
Each workout MUST have a 'name' field (like 'Push Day', 'Pull Day', etc.) and an 'excercises' array (note: excercises with TWO e's).

CRITICAL: For videoLink - put ONLY the exercise name as plain text. DO NOT include any URLs or links.
Example: ""videoLink"": ""bench press"" NOT ""videoLink"": ""https://...""

DO NOT RETURN ANY OTHER TEXT EXCEPT THE JSON ARRAY.";

    try
    {
        Console.WriteLine("[WORKOUTS] Sending request to Gemini...");

        // שולחים את הבקשה למודל gemini-2.5-flash
        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: contents
        );

        // מוציאים את הטקסט מהתשובה ומנקים אותו מסימוני markdown (```json)
        var resultText = response.Candidates[0].Content.Parts[0].Text;
        var cleanJson = CleanJsonString(resultText);

        Console.WriteLine($"[WORKOUTS] Received response from Gemini: {cleanJson.Substring(0, Math.Min(200, cleanJson.Length))}...");

        // ממירים את ה-JSON לרשימת אובייקטים של WorkoutData
        var workoutsData = JsonSerializer.Deserialize<List<WorkoutData>>(cleanJson);

        if (workoutsData != null && workoutsData.Count > 0)
        {
            Console.WriteLine($"[WORKOUTS] Parsed {workoutsData.Count} workouts. Saving to database...");

            // ── מחיקת התוכניות הישנות של המשתמש לפני שמירת חדשות ──
            var oldPlans = db.WorkoutPlans.Where(wp => wp.UserId == userId);
            var oldExercises = db.Exercises.Where(e => oldPlans.Select(wp => wp.Id).Contains(e.WorkoutPlanId));
            db.Exercises.RemoveRange(oldExercises);   // קודם מוחקים תרגילים
            db.WorkoutPlans.RemoveRange(oldPlans);    // ואז את התוכניות
            await db.SaveChangesAsync();

            // ── שמירת התוכניות החדשות, יום אחר יום ──
            for (int i = 0; i < workoutsData.Count; i++)
            {
                // יצירת תוכנית אימון ליום מסוים
                var workoutPlan = new WorkoutPlan
                {
                    UserId = userId,
                    Name = workoutsData[i].name ?? $"Day {i + 1}", // שם ברירת מחדל אם חסר
                    DayNumber = i + 1
                };

                // שומרים קודם את התוכנית כדי לקבל לה Id מהדאטהבייס
                db.WorkoutPlans.Add(workoutPlan);
                await db.SaveChangesAsync();

                // אם יש תרגילים - שומרים כל אחד מהם
                if (workoutsData[i].excercises != null)
                {
                    Console.WriteLine($"[WORKOUTS] Saving {workoutsData[i].excercises.Count} exercises for workout {i + 1}");

                    for (int j = 0; j < workoutsData[i].excercises.Count; j++)
                    {
                        var ex = workoutsData[i].excercises[j];
                        db.Exercises.Add(new Exercise
                        {
                            WorkoutPlanId = workoutPlan.Id,         // קישור לתוכנית
                            Name = ex.name ?? "Unknown Exercise",
                            Sets = ex.sets,
                            Reps = ex.reps,
                            RestTime = ex.restTime,
                            VideoLink = ex.videoLink ?? ex.name ?? "",
                            OrderIndex = j                          // שומר את סדר התרגילים
                        });
                    }
                }
            }

            // שמירה סופית של כל התרגילים
            await db.SaveChangesAsync();
            Console.WriteLine("[WORKOUTS] Successfully saved all workouts to database!");
        }
        else
        {
            // המודל לא החזיר תוכניות תקינות
            Console.WriteLine("[WORKOUTS] WARNING: No workouts were parsed from Gemini response!");
        }

        // מחזירים ללקוח את ה-JSON הנקי שקיבלנו מ-Gemini
        return Results.Content(cleanJson, "application/json");
    }
    catch (Exception ex)
    {
        // טיפול בשגיאה כולל stack trace ללוג
        Console.WriteLine($"[WORKOUTS] ERROR: {ex.Message}");
        Console.WriteLine($"[WORKOUTS] Stack trace: {ex.StackTrace}");
        return Results.Problem($"Error: {ex.Message}");
    }
});

// ============================================================
// GET /replace-exercise  ─ מציאת תרגיל חלופי ע"י Gemini
// ============================================================
app.MapGet("/replace-exercise", async (string exerciseName) =>
{
    Console.WriteLine($"[REPLACE] Request to replace exercise: {exerciseName}");

    var client = new Client(apiKey: apiKey);

    // prompt ל-Gemini: מבקש תרגיל חלופי אחד בפורמט JSON מוגדר
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
        // שליחת הבקשה ל-Gemini
        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: contents
        );

        // ניקוי התשובה
        var resultText = response.Candidates[0].Content.Parts[0].Text;
        var cleanJson = CleanJsonString(resultText);

        Console.WriteLine($"[REPLACE] Alternative exercise found: {cleanJson}");

        // מחזירים ללקוח את התרגיל החלופי (לא נשמר בדאטהבייס)
        return Results.Content(cleanJson, "application/json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[REPLACE] ERROR: {ex.Message}");
        return Results.Problem($"Error: {ex.Message}");
    }
});

// מפעילים את השרת (חוסם עד שהאפליקציה נסגרת)
app.Run();

// ── Utils ─────────────────────────────────────────────────────────────────
// פונקציית עזר: מנקה תשובת JSON מ-Gemini מסימוני markdown ורווחים מיותרים
string CleanJsonString(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return input;

    return input
        .Replace("```json", "")  // מסיר פתיחת code block
        .Replace("```", "")      // מסיר סגירת code block
        .Trim();                 // מסיר רווחים בקצוות
}

// ============================================================
// DATABASE MODELS - מודל הדאטהבייס
// ============================================================

// ה-DbContext: השער לדאטהבייס. מגדיר אילו טבלאות (DbSet) קיימות.
public class WorkoutDbContext : DbContext
{
    public DbSet<User> Users { get; set; }                       // טבלת משתמשים
    public DbSet<WorkoutPlan> WorkoutPlans { get; set; }         // טבלת תוכניות אימון
    public DbSet<Exercise> Exercises { get; set; }              // טבלת תרגילים
    public DbSet<WorkoutProgress> WorkoutProgress { get; set; } // טבלת מעקב התקדמות

    // קונסטרקטור שמקבל את ההגדרות (סוג דאטהבייס וכו')
    public WorkoutDbContext(DbContextOptions<WorkoutDbContext> options) : base(options) { }

    // הגדרת המודל: מפתחות וקשרים בין טבלאות
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── FIX: PostgreSQL צריך ValueGeneratedOnAdd() כדי לייצר ID אוטומטית ──
        // מגדירים שכל מפתח ראשי (Id) ייווצר אוטומטית ע"י הדאטהבייס
        modelBuilder.Entity<User>()
            .Property(u => u.Id)
            .ValueGeneratedOnAdd();

        modelBuilder.Entity<WorkoutPlan>()
            .Property(wp => wp.Id)
            .ValueGeneratedOnAdd();

        modelBuilder.Entity<Exercise>()
            .Property(e => e.Id)
            .ValueGeneratedOnAdd();

        modelBuilder.Entity<WorkoutProgress>()
            .Property(wp => wp.Id)
            .ValueGeneratedOnAdd();

        // ── Relationships - הגדרת הקשרים בין הטבלאות ──────────────────────────

        // תוכנית אימון שייכת למשתמש אחד; למשתמש יש הרבה תוכניות (1-to-many)
        modelBuilder.Entity<WorkoutPlan>()
            .HasOne(wp => wp.User)
            .WithMany(u => u.WorkoutPlans)
            .HasForeignKey(wp => wp.UserId);

        // תרגיל שייך לתוכנית אחת; לתוכנית יש הרבה תרגילים (1-to-many)
        modelBuilder.Entity<Exercise>()
            .HasOne(e => e.WorkoutPlan)
            .WithMany(wp => wp.Exercises)
            .HasForeignKey(e => e.WorkoutPlanId);

        // רשומת התקדמות שייכת למשתמש אחד; למשתמש יש הרבה רשומות (1-to-many)
        modelBuilder.Entity<WorkoutProgress>()
            .HasOne(wp => wp.User)
            .WithMany(u => u.Progress)
            .HasForeignKey(wp => wp.UserId);
    }
}

// ============================================================
// Entities - הישויות (כל מחלקה = טבלה בדאטהבייס)
// ============================================================

// משתמש במערכת
public class User
{
    [Key]                                                  // מפתח ראשי
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]  // נוצר אוטומטית
    public int Id { get; set; }

    [Required]                          // שדה חובה
    public string Username { get; set; }

    [Required]
    public string Password { get; set; } // ⚠️ נשמר כטקסט גלוי - לא מאובטח

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow; // תאריך יצירה

    // קשרים: רשימת תוכניות האימון והתקדמות של המשתמש
    public List<WorkoutPlan> WorkoutPlans { get; set; } = new();
    public List<WorkoutProgress> Progress { get; set; } = new();
}

// תוכנית אימון (יום אימון אחד)
public class WorkoutPlan
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }      // מפתח זר - למי שייכת התוכנית

    [Required]
    public string Name { get; set; }     // שם היום (Push Day וכו')

    public int DayNumber { get; set; }   // מספר היום בתוכנית

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // קשרים: המשתמש הבעלים, ורשימת התרגילים בתוכנית
    public User User { get; set; }
    public List<Exercise> Exercises { get; set; } = new();
}

// תרגיל בודד בתוך תוכנית אימון
public class Exercise
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int WorkoutPlanId { get; set; }  // מפתח זר - לאיזו תוכנית שייך

    [Required]
    public string Name { get; set; }        // שם התרגיל

    public int Sets { get; set; }           // מספר סטים
    public int Reps { get; set; }           // מספר חזרות
    public int RestTime { get; set; }       // זמן מנוחה (שניות)
    public string VideoLink { get; set; }   // קישור/שם לסרטון הדגמה
    public int OrderIndex { get; set; }     // סדר התרגיל בתוכנית

    // קשר: התוכנית שאליה שייך התרגיל
    public WorkoutPlan WorkoutPlan { get; set; }
}

// רשומת מעקב התקדמות (אימון שבוצע)
// ⚠️ הערה: הישות מוגדרת אך אין endpoint שכותב אליה בקוד הזה.
public class WorkoutProgress
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }          // מפתח זר - למי שייכת הרשומה

    [Required]
    public string ExerciseName { get; set; } // שם התרגיל שבוצע

    public int Sets { get; set; }            // סטים שבוצעו
    public int Reps { get; set; }            // חזרות שבוצעו
    public double Weight { get; set; }       // המשקל שהורם
    public string Notes { get; set; }        // הערות
    public DateTime CompletedAt { get; set; }// מתי בוצע

    // קשר: המשתמש הבעלים
    public User User { get; set; }
}

// ============================================================
// DTOs - אובייקטים להעברת נתונים (Data Transfer Objects)
// ============================================================

// פרטי התחברות/רישום שמגיעים מהלקוח
public record UserCredentials(string username, string password);

// מבנה תוכנית אימון כפי שמגיע מ-Gemini (לצורך deserialization)
public class WorkoutData
{
    public string name { get; set; }
    public List<ExerciseData> excercises { get; set; } // שים לב: excercises עם שני e
}

// מבנה תרגיל כפי שמגיע מ-Gemini
public class ExerciseData
{
    public string name { get; set; }
    public int sets { get; set; }
    public int reps { get; set; }
    public int restTime { get; set; }
    public string videoLink { get; set; }
}

// DTO למעקב התקדמות
// ⚠️ הערה: מוגדר אך לא בשימוש בקוד הזה (אין endpoint שמשתמש בו).
public class WorkoutProgressDto
{
    public int userId { get; set; }
    public string exerciseName { get; set; }
    public int sets { get; set; }
    public int reps { get; set; }
    public double weight { get; set; }
    public string notes { get; set; }
}
