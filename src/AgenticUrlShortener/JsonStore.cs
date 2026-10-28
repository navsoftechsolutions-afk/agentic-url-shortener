using System.Text.Json;
namespace AgenticUrlShortener;
public class JsonStore<T> where T:new() {
 private readonly string _path;private readonly SemaphoreSlim _gate=new(1,1);
 private static readonly JsonSerializerOptions Options=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
 public JsonStore(string path){_path=path;Directory.CreateDirectory(Path.GetDirectoryName(path)!);}
 public async Task<R> Update<R>(Func<T,R> action){await _gate.WaitAsync();try {var data=await LoadUnsafe();var result=action(data);await SaveUnsafe(data);return result;}finally{_gate.Release();}}
 public async Task<R> Read<R>(Func<T,R> action){await _gate.WaitAsync();try{return action(await LoadUnsafe());}finally{_gate.Release();}}
 private async Task<T> LoadUnsafe()=>File.Exists(_path)?JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(_path),Options)??new T():new T();
 private async Task SaveUnsafe(T value){var tmp=_path+".tmp";await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(value,Options));File.Move(tmp,_path,true);}
}
public class WorkflowCollection {public Dictionary<string,WorkflowState> Items {get;set;}=new();}
