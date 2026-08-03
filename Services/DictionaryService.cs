using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// 聚合词典服务。
    /// 优先查询已加载的 MDict；若未加载或未命中，则回退到 ECDICT；仍未命中返回 <c>null</c>。
    /// </summary>
    public class DictionaryService : IDictionaryService
    {
        private readonly MDictService _mdictService;
        private readonly EcDictService _ecdictService;

        /// <inheritdoc />
        public bool IsLoaded => (_mdictService?.IsLoaded ?? false) || (_ecdictService?.IsLoaded ?? false);

        /// <summary>
        /// 初始化 <see cref="DictionaryService"/>。
        /// </summary>
        /// <param name="mdictService">MDict 服务实例。</param>
        /// <param name="ecdictService">ECDICT 服务实例。</param>
        public DictionaryService(MDictService mdictService, EcDictService ecdictService)
        {
            _mdictService = mdictService ?? throw new ArgumentNullException(nameof(mdictService));
            _ecdictService = ecdictService ?? throw new ArgumentNullException(nameof(ecdictService));
        }

        /// <inheritdoc />
        public void Load()
        {
            _mdictService.Load();
            _ecdictService.Load();
        }

        /// <inheritdoc />
        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.WhenAll(
                _mdictService.LoadAsync(cancellationToken),
                _ecdictService.LoadAsync(cancellationToken));
        }

        /// <inheritdoc />
        public IWordEntry Lookup(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return null;

            // 1. 优先 MDict
            if (_mdictService.IsLoaded)
            {
                var mdictResult = _mdictService.Lookup(word);
                if (mdictResult != null)
                    return mdictResult;
            }

            // 2. 回退 ECDICT
            if (_ecdictService.IsLoaded)
            {
                return _ecdictService.Lookup(word);
            }

            return null;
        }

        /// <inheritdoc />
        public IEnumerable<string> GetSuggestions(string prefix, int maxCount)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return Enumerable.Empty<string>();

            return _ecdictService.GetSuggestions(prefix, maxCount);
        }
    }
}
