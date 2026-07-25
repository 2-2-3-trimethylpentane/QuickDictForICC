namespace QuickDictForICC.Services
{
    /// <summary>
    /// 查词服务接口。
    /// </summary>
    public interface IDictionaryService
    {
        /// <summary>是否已成功加载词典数据。</summary>
        bool IsLoaded { get; }

        /// <summary>
        /// 加载词典数据。若文件不存在或服务不可用，应优雅降级（IsLoaded=false）。
        /// </summary>
        void Load();

        /// <summary>
        /// 异步加载词典数据。耗时操作在后台线程执行，避免阻塞 UI。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>表示异步加载操作的任务。</returns>
        System.Threading.Tasks.Task LoadAsync(System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// 查询指定单词。未命中时返回 <c>null</c>。
        /// </summary>
        /// <param name="word">要查询的单词。</param>
        /// <returns>单词条目；未找到时返回 <c>null</c>。</returns>
        IWordEntry Lookup(string word);
    }
}
