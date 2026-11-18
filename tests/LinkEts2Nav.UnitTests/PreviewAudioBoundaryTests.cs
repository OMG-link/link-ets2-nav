using System.Reflection;
using LinkEts2Nav.Pages;
namespace LinkEts2Nav.UnitTests;
[CollectionDefinition("AudioPreviewSettings",DisableParallelization=true)] public sealed class AudioPreviewSettingsCollection { }
[Collection("AudioPreviewSettings")]
public sealed class PreviewAudioBoundaryTests:IDisposable
{
 readonly string config=Path.Combine(AppContext.BaseDirectory,"Settings","LinkEts2Nav.Audio.json");
 readonly byte[]? original;
 public PreviewAudioBoundaryTests(){Directory.CreateDirectory(Path.GetDirectoryName(config)!);if(File.Exists(config))original=File.ReadAllBytes(config);}
 public void Dispose(){if(original is null)File.Delete(config);else File.WriteAllBytes(config,original);}
 static Task Preview(Diagnostics page)=>(Task)typeof(Diagnostics).GetMethod("PreviewAudio",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(page,null)!;
 static T Field<T>(Diagnostics page,string name)=>(T)typeof(Diagnostics).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(page)!;
 [Theory][InlineData("{")][InlineData("null")][InlineData("{\"filePath\":\"invalid.fsb\"}")]
 public async Task KnownConfigurationErrorsDisplayTheRealReasonAndReleaseTheButton(string json)
 {
  File.WriteAllText(config,json);await using var page=new Diagnostics();
  await Preview(page);Assert.StartsWith("试听失败：",Field<string>(page,"audioMessage"));Assert.False(Field<bool>(page,"previewing"));
 }
 [Fact] public async Task MissingConfiguredMediaDisplaysItsPathAndCanRetry()
 {
  string missing=Path.Combine(AppContext.BaseDirectory,"preview-missing-"+Guid.NewGuid()+".wav");File.WriteAllText(config,System.Text.Json.JsonSerializer.Serialize(new{filePath=missing}));
  await using var page=new Diagnostics();await Preview(page);Assert.Contains(missing,Field<string>(page,"audioMessage"));Assert.False(Field<bool>(page,"previewing"));
  File.WriteAllText(config,"{\"filePath\":\"retry.fsb\"}");await Preview(page);Assert.Contains("WAV, OGG, MP3 or FLAC",Field<string>(page,"audioMessage"));Assert.False(Field<bool>(page,"previewing"));
 }
 [Fact] public async Task ConfiguredDirectoryIsReportedAsMissingMediaWithoutPlayingAudio()
 {
  string directory=Path.Combine(AppContext.BaseDirectory,"preview-missing-dir-"+Guid.NewGuid()+".wav");Directory.CreateDirectory(directory);
  try{File.WriteAllText(config,System.Text.Json.JsonSerializer.Serialize(new{filePath=directory}));await using var page=new Diagnostics();await Preview(page);Assert.StartsWith("试听失败：",Field<string>(page,"audioMessage"));Assert.False(Field<bool>(page,"previewing"));}
  finally{Directory.Delete(directory);}
 }
 [Fact] public async Task OwnedPageCancellationReleasesTheButton()
 {
  await using var page=new Diagnostics();Field<CancellationTokenSource>(page,"stop").Cancel();await Preview(page);Assert.False(Field<bool>(page,"previewing"));
 }
 [Fact] public async Task UnknownPageFailurePropagatesAndStillReleasesTheButton()
 {
  await using var page=new Diagnostics();var field=typeof(Diagnostics).GetField("stop",BindingFlags.Instance|BindingFlags.NonPublic)!;var original=field.GetValue(page);field.SetValue(page,null);
  try{await Assert.ThrowsAsync<NullReferenceException>(()=>Preview(page));Assert.False(Field<bool>(page,"previewing"));Assert.DoesNotContain("试听失败",Field<string>(page,"audioMessage"));}
  finally{field.SetValue(page,original);}
 }
 [Fact] public async Task BusyConfigurationFileDisplaysTheActualReadFailureAndReleasesTheButton()
 {
  File.WriteAllText(config,"{}");using var locked=File.Open(config,FileMode.Open,FileAccess.ReadWrite,FileShare.None);await using var page=new Diagnostics();
  await Preview(page);Assert.StartsWith("试听失败：",Field<string>(page,"audioMessage"));Assert.Contains("LinkEts2Nav.Audio.json",Field<string>(page,"audioMessage"));Assert.False(Field<bool>(page,"previewing"));
 }}