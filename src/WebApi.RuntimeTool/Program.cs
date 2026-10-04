if(args.Length!=2||args[0]!="probe"||!Uri.TryCreate(args[1],UriKind.Absolute,out var url)||url.Scheme is not("http" or "https")||url.UserInfo.Length>0){System.Environment.ExitCode=2;return;}
try{using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false};using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5)};using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);System.Environment.ExitCode=response.IsSuccessStatusCode?0:1;}
catch(HttpRequestException){System.Environment.ExitCode=1;}
catch(TaskCanceledException){System.Environment.ExitCode=1;}
