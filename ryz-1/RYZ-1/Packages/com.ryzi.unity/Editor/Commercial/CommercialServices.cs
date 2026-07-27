using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryzi.Editor
{
    public enum EntitlementStatus
    {
        LocalBasic,
        Indie,
        Studio,
        Enterprise,
        Unknown
    }

    public interface IEntitlementService
    {
        EntitlementStatus GetCurrentStatus();
        Task RefreshAsync(CancellationToken cancellationToken);
    }

    public sealed class LocalEntitlementService : IEntitlementService
    {
        public EntitlementStatus GetCurrentStatus() => EntitlementStatus.LocalBasic;
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Serializable]
    public sealed class ModelPackageInfo
    {
        public string id;
        public string version;
        public string displayName;
    }

    public interface IModelDistributionService
    {
        Task<ModelPackageInfo[]> ListAvailableAsync(CancellationToken cancellationToken);
    }

    public sealed class LocalModelDistributionService : IModelDistributionService
    {
        public Task<ModelPackageInfo[]> ListAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<ModelPackageInfo>());
    }
}
