using WebApi.NotificationFixtureHost;
if(args is ["rebind-journal",var directory,var oldOwner,var oldProject,var newOwner,var newProject])FixtureJournal.Rebind(directory,oldOwner,oldProject,newOwner,newProject);
else if(args is ["seed-notification-event",var ruleId]){try{System.Environment.ExitCode=await NotificationAcceptanceCommand.RunAsync(ruleId);}catch{Console.Error.WriteLine("Owned notification seed failed; inspect fixture configuration and identity.");System.Environment.ExitCode=2;}}
else await NotificationFixtureApp.Build(args).RunAsync();
