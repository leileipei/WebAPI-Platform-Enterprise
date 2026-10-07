using WebApi.NotificationFixtureHost;
if(args is ["rebind-journal",var directory,var oldOwner,var oldProject,var newOwner,var newProject])FixtureJournal.Rebind(directory,oldOwner,oldProject,newOwner,newProject);
else if(args is ["seed-notification-event",var ruleId])System.Environment.ExitCode=await NotificationAcceptanceCommand.RunAsync(ruleId);
else await NotificationFixtureApp.Build(args).RunAsync();
