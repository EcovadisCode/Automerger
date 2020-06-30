// -----------------------------------------------------------------------------
// ARGUMENTS
// -----------------------------------------------------------------------------

var sourceBranch = Argument("sourceBranch", "");
var targetBranch = Argument("targetBranch", "");

var sourceBranchIndex = Argument("sourceBranchIndex", 1);
var targetBranchIndex = Argument("targetBranchIndex", 0);

var tempBranch = Argument("tempBranch", "");

var automergeConfigFile = Directory("./") + File(".automergeConfig");

//////////////////////////////////////////////////////////////////////
// AUTOMATIC MERGES
//////////////////////////////////////////////////////////////////////

public class Team
{
    public string Channel { get; set; }
    public string Name { get; set; }
    public List<string> TeamMembers { get; set; }

    public bool IsMember(string email)
    {
        return TeamMembers.Any(x => x.Contains(email));
    }

    public static Team GetDefault()
    {
        return new Team()
        {
            Channel = "DefaultChannel",
            Name = "DefaultTeam",
            TeamMembers = new List<string>() { "@" }
        };
    }
}

public class Branch
{
    private static readonly string _releaseBranchPrefix = "release/release-";
    private static readonly string _remotesBranchPrefix = "remotes/origin/";
    private static readonly string _remoteReleaseBranchPrefix = $"{_remotesBranchPrefix}{_releaseBranchPrefix}";

    public Branch(Version v)
    {
        FullName = _remotesBranchPrefix + v;
    }

    public Branch(string fullname)
    {
        FullName = fullname.Trim();
    }

    public string FullName { get; private set; }

    public string Name
    {
        get { return _releaseBranchPrefix + Version; }
    }

    public Version Version
    {
        get { return new Version(FullName.Replace(_releaseBranchPrefix, "").Replace(_remotesBranchPrefix, "").Trim()); }
    }

    public bool IsReleaseBranch
    {
        get { return FullName.Contains(_remoteReleaseBranchPrefix); }
    }

    public bool IsMajorReleaseBranch
    {
        get { return FullName.Count(x => x == '.') == 1; }
    }

    public bool IsHotfixBranch
    {
        get { return FullName.Count(x => x == '.') == 2; }
    }

    public bool IsDevelop
    {
        get { return FullName.Contains("develop"); }
    }

    public Branch BumpVersion()
     {
         var v = Version;
         var minor = v.Minor == -1 ? 0 : v.Minor;
         var build = v.Build == -1 ? 0 : v.Build;

         if (IsReleaseBranch)
            return new Branch(new Version(v.Major, minor + 1, build));

         if(IsDevelop)
            return new Branch(new Version(v.Major, minor + 2, build));

        return new Branch(new Version(v.Major, minor, build + 1));
    }

    public static string FormatTempBranchName(string source, string target)
    {
        var sourceVersion = source.Replace(_releaseBranchPrefix, "").Replace(".", "");
        var targetVersion = target.Replace(_releaseBranchPrefix, "").Replace(".", "");
        return sourceVersion + "_into_" + targetVersion;
    }
}
    
public class AutomergeConfig
{
    public List<string> IgnoreBranches { get; set; }
    public List<Team> Teams { get; set; }

    public Team GetTeamByMember(string email)
    {
        var team = Teams.FirstOrDefault(x => x.IsMember(email));
        return team ?? Team.GetDefault();
    }
}

public AutomergeConfig LoadAutomergeConfig()
{
    if (!FileExists(automergeConfigFile))
    {
        Information("Missing .automergeConfig file, all existing branches are scaned!");
        return new AutomergeConfig()
        {
            IgnoreBranches = new List<string>(),
            Teams = new List<Team>() { Team.GetDefault() }
        };
    }

    var config = DeserializeJsonFromFile<AutomergeConfig>(automergeConfigFile);
    return config;
}

public void NotifyTeam(Team team, string message)
{
    Information("Notification sent to team {0} on channel {1}", team.Name, team.Channel);
    NotifyOnSlack(message, team.Channel, "SLACK_HOOK_URI_" + team.Name);
}

public List<string> GetConflicts(List<string> mergeOutput)
{
    const string conflictLabel = "CONFLICT (content): Merge conflict in";
    return mergeOutput
                    .Where(x => x.Contains(conflictLabel))
                    .Select(y => y.Replace(conflictLabel, "")
                    .Trim())
                    .ToList();
}

public bool NoChanges(List<string> mergeOutput)
{
    const string noChangesLabel = "already up to date";
    return mergeOutput.Any(x => x.ToLower().Contains(noChangesLabel));
}

public string ExtractValue(string input, string startingCharacter, string endCharacter)
{
    int pFrom = input.IndexOf(startingCharacter) + startingCharacter.Length;
    int pTo = input.LastIndexOf(endCharacter);

    return input.Substring(pFrom, pTo - pFrom);
}

public string GetLastModifierOfFile(string file)
{
    const string AuthorStartingLine = "Author: ";

    var output = GitCommand("log -1 " + file, false, true);
    var author = output
                    .Where(x => x.StartsWith(AuthorStartingLine))
                    .First()
                    .Replace(AuthorStartingLine, "")
                    .Trim();

    return ExtractValue(author, "<", ">");
}

public List<Branch> GetReleaseBranches()
{
    var config = LoadAutomergeConfig();

    var allBranches = GitCommand("branch -a");

    var releaseBranches = allBranches
                .Where(x => !config.IgnoreBranches.Any(x.Contains))
                .Select(x => new Branch(x.ToString()))
                .Where(x => x.IsReleaseBranch)
                .OrderByDescending(x => x.Version);

    return releaseBranches.ToList();
}

public void NotifyMergeConflicts(List<string> conflicts, AutomergeConfig config, string additionalMessage)
{
    var teams = new HashSet<Team>();

    GitCommand("reset --hard");
    GitCommand("checkout " + sourceBranch);

    var message = string.Format("Hi <!here> fellows! I found some conflicts during merge {0} to {1} \r\n", sourceBranch, targetBranch);
    foreach (var conflictFile in conflicts)
    {
        var conflictAuthorEmail = GetLastModifierOfFile(conflictFile);
        message += string.Format("Author: {1}; File: {0}\r\n", conflictFile, conflictAuthorEmail);
        var team = config.GetTeamByMember(conflictAuthorEmail);
        teams.Add(team);
    }
    message += "Can You please resolve them. Thanks :)";
	
	if(!string.IsNullOrEmpty(additionalMessage))
		message += $"\r\n{additionalMessage}";
	

    if (!teams.Any())
    {
        teams.Add(Team.GetDefault());
    }

    Information(message);
    foreach (var team in teams)
        NotifyTeam(team, message);
}

public string CreateGitCommandForConflictsResolution(string sourceBranch, string targetBranch)
{
	var branchForConflictResolution = $"{prefixForBranchForAutomergingConflictsResolution}_From_{sourceBranch}_To_{targetBranch}".Replace("/","-");
	string command =  "git fetch;";
	command += $"git checkout {targetBranch};";
	command +=  "git pull;";
	command += $"git checkout -B {branchForConflictResolution};";
	command +=  "git pull;";
	command += $"git merge {sourceBranch}; ";

	return command.Replace(";", Environment.NewLine);
}

public string GetCurrentBranchName()
{
    return GitCommand(" rev-parse --abbrev-ref HEAD " + sourceBranch, true, true).First();
}

public Branch GetLatestReleaseBranch()
{
    return GetReleaseBranches().First();
}

public void CheckoutBranch(string branch)
{
	GitCommand("fetch");
    GitCommand($"checkout {branch}");
    GitCommand("pull");
	Information($"Branch '{branch}' checked out");
}

public List<string> GitCommand(string arguments, bool failOnError = true, bool RedirectStandardOutput = true){
    Information("Execute: git {0}", arguments);

    using(var process = StartAndReturnProcess("git", new ProcessSettings{ 
        Arguments = arguments,
        RedirectStandardOutput = RedirectStandardOutput,
        RedirectStandardError = true,
        Timeout = 1000 * 60 * 10
        }))
    {
        process.WaitForExit();
        var output = process.GetStandardOutput().ToList();
        if(process.GetExitCode() != 0){
            Information("Exitcode: {0}", process.GetExitCode());
            Information("Error: {0}", string.Join("\r\n", process.GetStandardError()));
            Information("Output: {0}", string.Join("\r\n", process.GetStandardOutput()));
        
            if (failOnError)
            {
                var message = "Unable to run git! Build failed!";
                Error(message);
                throw new Exception(message);
            }
        }
        return output;
    }
}

Task("BranchSync")
    .Does(() => {
        var config = LoadAutomergeConfig();

        GitCommand("fetch");
        Information("BranchSync 1/8 - Fetch");
        
        GitCommand("checkout -f " + sourceBranch);
        Information("BranchSync 2/8 - checkout source: {0}", sourceBranch);

        GitCommand("pull");
        Information("BranchSync 3/8 - pull source: {0}", sourceBranch);

        GitCommand("branch -D " + tempBranch, false);
        Information("BranchSync 4/8 - delete temp: {0}", tempBranch);

        GitCommand("checkout -f " + targetBranch);
        Information("BranchSync 5/8 - checkout target: {0}", targetBranch);

        GitCommand("pull");
        Information("BranchSync 6/8 - pull target: {0}", targetBranch);

        GitCommand("checkout -B " + tempBranch);
        Information("BranchSync 7/8 - create temp branch: {0}", tempBranch);

        var mergeOutput = GitCommand("merge -Xignore-space-change " + sourceBranch, false, true);
        Information("BranchSync 8/8 - merge source branch: {0} to temp branch: {1}",  sourceBranch, tempBranch);

        if(NoChanges(mergeOutput)){
            throw new Exception("Nothing to merge. Stopping build.");
        }

        var conflicts = GetConflicts(mergeOutput);
        if(conflicts.Any()){
            Information("Found conflicts during merge: {0} to temp branch: {1}!", sourceBranch, tempBranch);
			
			string additionalMessage = null;
			try
			{
				var gitCommandForConflictsResolution = CreateGitCommandForConflictsResolution(sourceBranch, targetBranch);
				additionalMessage = $"Use those git commmands (on clean branch) to create a branch for conflicts resolution:\r\n{gitCommandForConflictsResolution}";
				Information(additionalMessage);
			}			
			catch(Exception e)
			{
				Information($"ERROR: Problem while creating pull request for merge conflicts: {e.Message}");
			}
			
            NotifyMergeConflicts(conflicts, config, additionalMessage);
            throw new Exception("Conflict found. Stopping build. Please resolve conflicts manually.");
        }
    });

Task("MergeTempToTargetBranch").Does(() => {
    GitCommand("checkout -f " + targetBranch);
    Information("MergeTempToTargetBranch 1/4 - checkout " + targetBranch);

    GitCommand("pull");
    Information("MergeTempToTargetBranch 2/4 - pull");

    GitCommand("merge -Xignore-space-change " + tempBranch);
    Information("MergeTempToTargetBranch 3/4 - merge " + tempBranch);

    GitCommand("push origin " + targetBranch);
    Information("MergeTempToTargetBranch 4/4 - push origin " + targetBranch);

    NotifyOnSlack("Merge " + tempBranch + " done!", "jedi_merges");
});

Task("FindBranches").Does(() => {
    var branches = GetReleaseBranches();
    sourceBranch = String.IsNullOrEmpty(sourceBranch) ? branches[sourceBranchIndex].Name : sourceBranch;
    targetBranch = String.IsNullOrEmpty(targetBranch) ? branches[targetBranchIndex].Name : targetBranch;

    if(string.IsNullOrEmpty(sourceBranch))
        throw new Exception("Missing sourceBranch argument");

    if(string.IsNullOrEmpty(targetBranch))
        throw new Exception("Missing targetBranch argument");

    tempBranch = String.IsNullOrEmpty(tempBranch) ? Branch.FormatTempBranchName(sourceBranch, targetBranch) : tempBranch;

    Information("Source branch: {0}", sourceBranch);
    Information("Target branch: {0}", targetBranch);
    Information("Temp branch: {0}", tempBranch);
});

Task("SyncBranches")
    .IsDependentOn("FindBranches")
    .IsDependentOn("BranchSync")
    .IsDependentOn("Default")
    .IsDependentOn("MergeTempToTargetBranch")
    .OnError(exception =>
    {
        NotifyOnSlack("<!Owner> Exception while merging: " + tempBranch + ". Exception: " + exception.ToString(), "jedi_merges");
    });