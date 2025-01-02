using System.Net;
using System.Text.RegularExpressions;
using CliWrap;

public class Program
{
    public static async Task Main(string[] args)
    {
        var email = "your-email";
        var password = "your-password";

        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler();
        handler.CookieContainer = cookieContainer;
        var httpClient = new HttpClient(handler);
        
        var mainPageContent = await UchaSeLoginAsync(httpClient, email, password);
        var courseUrls = GetCourseUrls(mainPageContent);

        courseUrls = courseUrls.Where(s => !s.Contains("frenski") && 
                                           !s.Contains("nemski") && 
                                           !s.Contains("kitayski") && 
                                           !s.Contains("angliyski") && 
                                           !s.Contains("ruski")).ToList();

        courseUrls.Reverse();
        
        for (var index = 0; index < courseUrls.Count; index++)
        {
            var courseUrl = courseUrls[index];
            await DownloadVideosAsync(httpClient, courseUrl, cookieContainer);
            Console.WriteLine($" Total: {index}/{courseUrls.Count} courses downloaded");
        }
    }

    private static List<string> GetCourseUrls(string mainPageContent)
    {
        var urlPattern = new Regex("https:\\/\\/ucha.se\\/videos\\/(.+?)\\/(.+?)\\/");
        var urlMatches = urlPattern.Matches(mainPageContent);
        var listMatchedUrls = urlMatches.Select(match => match.Groups[0].Value).ToList();
        Console.WriteLine($"Found {listMatchedUrls.Count} courses");
        return listMatchedUrls;
    }

    private static async Task DownloadVideosAsync(HttpClient httpClient, string urlCourse, CookieContainer cookieContainer)
    {
        var classAndCourseName = Path.Combine(urlCourse.Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(2).Reverse().ToArray());
        Console.Write(classAndCourseName);

        var coursePageResponse = await httpClient.GetStringAsync(urlCourse);
        var videoUrls = GetVideosUrls(coursePageResponse);
        var folderName = Path.Combine("videos", classAndCourseName);
        Console.Write($" has {videoUrls.Count} videos");
        
        var phpSession = cookieContainer.GetCookies(new Uri("https://ucha.se"))["PHPSESSID"].Value;

        if (!Directory.Exists(folderName))
        {
            Directory.CreateDirectory(folderName);
        }

        for (var i = 0; i < videoUrls.Count; i++)
        {
            var videoUrl = videoUrls[i];
            var outputFileName = Path.Combine(folderName, (i+1) + "_" + videoUrl.Split('/').Last()) + ".mp4";
            if (File.Exists(outputFileName))
            {
                Console.Write($" {i+1}^");
                continue;
            }
            
            var chunkFileUrl = await GetM3U8UrlAsync(httpClient, videoUrl);
            
            if (string.IsNullOrEmpty(chunkFileUrl))
            {
                Console.Write($" {i+1}@");
                continue;
            }
        
            await ProcessFfmpegCommandAsync(phpSession, chunkFileUrl, outputFileName));
            Console.Write($" {i+1}");
        }

        Console.WriteLine();
    }

    private static async Task<string> GetM3U8UrlAsync(HttpClient httpClient, string videoUrl)
    {
        try
        {
            var videoPageContent = await httpClient.GetStringAsync(videoUrl);
            var match = Regex.Match(videoPageContent,
                @"(https:\\\/\\\/proxy[1|2].ucha.se:443\\\/.+?playlist.m3u8).+?;s=([a-z0-9]+)&amp;ut=([a-zA-Z0-9]+)");
            var m3u8Url = match.Groups[1].Value.Replace(@"\/", "/");
            var m3u8Content = await httpClient.GetStringAsync(m3u8Url);
            var sessionKey = match.Groups[2].Value;
            var utKey = match.Groups[3].Value;
            var chunkFile = GetMaxResolutionChunkFile(m3u8Content);
            var chunkFileUrl = m3u8Url.Replace("playlist.m3u8", "") + chunkFile + "?s=" + sessionKey + "&ut=" + utKey;
            return chunkFileUrl;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Unable to process {videoUrl}, e: {e.Message}");
            return string.Empty;
        }
    }

    private static async Task<string> UchaSeLoginAsync(HttpClient httpClient, string email, string password)
    {
        // Step 1: Send GET request to login page
        var loginPageResponse = await httpClient.GetStringAsync("https://ucha.se/login/");

        // Step 2: Parse the response to find the hidden field _login_token
        var tokenPattern = @"<input type=""hidden"" name=""_login_token"" value=""(.+?)""\/>";
        var tokenMatch = Regex.Match(loginPageResponse, tokenPattern);
        var loginToken = tokenMatch.Groups[1].Value;

        // Step 3: Prepare the POST request with form data
        var postData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("_login_token", loginToken),
            new KeyValuePair<string, string>("email", email),
            new KeyValuePair<string, string>("password", password)
        });

        // Step 4: Send the POST request
        var postResponse = await httpClient.PostAsync("https://ucha.se/login/", postData);
        return await postResponse.Content.ReadAsStringAsync();
    }

    private static Task<CommandResult> ProcessFfmpegCommandAsync(string phpSessionId, string m3uUrl, string outputFileName)
    {
        var arguments = $@"-cookies ""PHPSESSID={phpSessionId}"" -user_agent ""Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Safari/537.36"" -i ""{m3uUrl}"" -c copy -bsf:a aac_adtstoasc {outputFileName}";
        
        var command= Cli.Wrap("ffmpeg")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None);
            
        return command.ExecuteAsync();
    }
    private static List<string> GetVideosUrls(string pageSource)
    {
        var urlPattern = @"<a href=""(https:\/\/ucha\.se\/watch\/.+?)""";
        var rg = new Regex(urlPattern);

        var matchedUrls = rg.Matches(pageSource);

        var listMatchedUrls = matchedUrls.Select(match => match.Groups[1].Value).ToList();
        return listMatchedUrls;
    }

    private static string GetMaxResolutionChunkFile(string m3u8Content)
    {
        var resolutionRegex = new Regex(@"RESOLUTION=(\d+x\d+)");
        var chunkListRegex = new Regex("(chunklist_.+?.m3u8)");
        var max = m3u8Content
            .Split('#')
            .Where(s => resolutionRegex.IsMatch(s))
            .OrderByDescending(s => int.Parse(resolutionRegex.Match(s).Groups[1].Value.Split('x').First()))
            .First();

        var chunkFileRelativeUrl = chunkListRegex.Match(max).Groups[1].Value;
        return chunkFileRelativeUrl;
    }
}