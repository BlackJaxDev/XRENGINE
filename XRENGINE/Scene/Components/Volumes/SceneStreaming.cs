using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Components.Scene.Volumes
{
	/// <summary>
	/// Streams an <see cref="XRScene"/> into and out of the current <see cref="XRWorldInstance"/>
	/// when something overlaps this volume.
	/// </summary>
	[Description("Streams an XRScene into/out of the current world based on overlaps.")]
	public class SceneStreamingVolumeComponent : TriggerVolumeComponent
	{
		private string _sceneAssetPath = string.Empty;

		/// <summary>
		/// Path to the scene asset to stream.
		/// If relative, it is treated as relative to <see cref="Engine.Assets"/>' game assets path.
		/// </summary>
		public string SceneAssetPath
		{
			get => _sceneAssetPath;
			set => SetField(ref _sceneAssetPath, value);
		}

		public bool LoadOnEnter { get; set; } = true;
		public bool UnloadOnLeave { get; set; } = true;

		private XRScene? _loadedScene;
		private bool _loadedInWorld;
		private int _loadToken;

		protected override void OnEntered(XRComponent component)
		{
			base.OnEntered(component);

			if (!LoadOnEnter)
				return;

			// Only load on the first overlap.
			if (OverlappingComponents.Count != 1)
				return;

			int token = Interlocked.Increment(ref _loadToken);
			_ = LoadAndAttachAsync(token);
		}

		protected override void OnLeft(XRComponent component)
		{
			base.OnLeft(component);

			if (!UnloadOnLeave)
				return;

			// Only unload when the last overlap leaves.
			if (OverlappingComponents.Count != 0)
				return;

			int token = Interlocked.Increment(ref _loadToken);
			Engine.EnqueueMainThreadTask(() => DetachIfLoaded(token, expectedWorld: World));
		}

		protected internal override void OnComponentDeactivated()
		{
			int token = Interlocked.Increment(ref _loadToken);
			Engine.EnqueueMainThreadTask(() => DetachIfLoaded(token, expectedWorld: World));
			base.OnComponentDeactivated();
		}

		private async Task LoadAndAttachAsync(int token)
		{
			XRWorldInstance? expectedWorld = World;
			if (expectedWorld is null)
				return;

			string? resolvedPath = ResolveSceneAssetPath(SceneAssetPath);
			if (string.IsNullOrWhiteSpace(resolvedPath))
				return;

			XRScene? scene = await Engine.Assets.LoadAsync<XRScene>(resolvedPath).ConfigureAwait(false);
			if (scene is null)
				return;

			Engine.EnqueueMainThreadTask(() => AttachIfEligible(token, expectedWorld, scene));
		}

		private void AttachIfEligible(int token, XRWorldInstance expectedWorld, XRScene scene)
		{
			if (token != _loadToken)
				return;

			// Component/world changed or we no longer have overlaps.
			if (!ReferenceEquals(World, expectedWorld))
				return;

			if (OverlappingComponents.Count == 0)
				return;

			if (_loadedInWorld)
				return;

			// Ensure the scene will actually attach its nodes.
			scene.IsVisible = true;

			_loadedScene = scene;
			expectedWorld.LoadScene(scene);
			_loadedInWorld = true;
		}

		private void DetachIfLoaded(int token, XRWorldInstance? expectedWorld)
		{
			if (token != _loadToken)
				return;

			if (!_loadedInWorld)
				return;

			if (expectedWorld is null)
				return;

			if (!ReferenceEquals(World, expectedWorld))
				return;

			XRScene? scene = _loadedScene;
			if (scene is null)
				return;

			expectedWorld.UnloadScene(scene);
			_loadedInWorld = false;
		}

		private static string? ResolveSceneAssetPath(string? input)
		{
			if (string.IsNullOrWhiteSpace(input))
				return null;

			string trimmed = input.Trim();
			if (Path.IsPathFullyQualified(trimmed) || Path.IsPathRooted(trimmed))
				return trimmed;

			string fromGameAssets = Path.Combine(Engine.Assets.GameAssetsPath, trimmed.Replace('/', Path.DirectorySeparatorChar));
			if (File.Exists(fromGameAssets))
				return fromGameAssets;

			if (!Path.HasExtension(fromGameAssets))
			{
				string withExt = $"{fromGameAssets}.{AssetManager.AssetExtension}";
				if (File.Exists(withExt))
					return withExt;
			}

			// Fall back to the combined path even if it doesn't exist yet (remote load, etc.).
			return Path.HasExtension(fromGameAssets)
				? fromGameAssets
				: $"{fromGameAssets}.{AssetManager.AssetExtension}";
		}
	}
}
