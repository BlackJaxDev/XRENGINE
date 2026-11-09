using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the DXGI factory and the Direct3D 11 device that the DXGI present path uses.
/// The desktop window and each detached ImGui window have their own
/// <see cref="VulkanDxgiPresenter"/>, but they share one device of this type.
/// </summary>
/// <remarks>
/// The device uses reference counting. Each presenter holds one reference and releases
/// it when it is disposed, so the device lives until the last presenter goes. The
/// immediate context is multithread protected, because presenters can call it from
/// more than one thread.
/// </remarks>
internal sealed unsafe class VulkanDxgiDevice
{
    private DXGI? _dxgi;
    private D3D11? _d3d11;
    private IDXGIFactory4* _factory;
    private ID3D11Device* _device;
    private ID3D11Device5* _device5;
    private ID3D11DeviceContext* _context;
    private ID3D11DeviceContext4* _context4;
    private int _references = 1;

    private VulkanDxgiDevice()
    {
    }

    /// <summary>The DXGI factory that creates the swapchains.</summary>
    internal IDXGIFactory4* Factory => _factory;

    /// <summary>The Direct3D 11 device that owns the shared textures and fences.</summary>
    internal ID3D11Device* Device => _device;

    /// <summary>The device interface that creates shared fences.</summary>
    internal ID3D11Device5* Device5 => _device5;

    /// <summary>The immediate context that copies and presents.</summary>
    internal ID3D11DeviceContext* Context => _context;

    /// <summary>The immediate context interface that waits for and signals fences.</summary>
    internal ID3D11DeviceContext4* Context4 => _context4;

    /// <summary>True when the DXGI factory reports tearing support for variable refresh displays.</summary>
    internal bool TearingSupported { get; private set; }

    /// <summary>True after the last reference is released.</summary>
    internal bool IsReleased => _references <= 0;

    /// <summary>
    /// Creates a device on the adapter of the Vulkan device. When this returns false,
    /// <paramref name="reason"/> tells why and no native object remains.
    /// </summary>
    internal static bool TryCreate(VulkanDeviceContext device, out VulkanDxgiDevice? created, out string reason)
    {
        created = null;
        VulkanDxgiDevice candidate = new();
        try
        {
            if (!candidate.Initialize(device, out reason))
            {
                candidate.Release();
                return false;
            }
        }
        catch (Exception exception)
        {
            candidate.Release();
            reason = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }

        created = candidate;
        return true;
    }

    /// <summary>Adds a reference for one more presenter.</summary>
    internal void AddReference()
    {
        if (_references <= 0)
            throw new ObjectDisposedException(nameof(VulkanDxgiDevice));
        _references++;
    }

    /// <summary>Releases one reference. The last release frees every native object.</summary>
    internal void Release()
    {
        if (_references <= 0)
            return;
        if (--_references > 0)
            return;

        if (_context is not null)
        {
            _context->ClearState();
            _context->Flush();
        }
        ReleaseCom(ref _context4);
        ReleaseCom(ref _context);
        ReleaseCom(ref _device5);
        ReleaseCom(ref _device);
        ReleaseCom(ref _factory);
        _d3d11?.Dispose();
        _d3d11 = null;
        _dxgi?.Dispose();
        _dxgi = null;
    }

    private bool Initialize(VulkanDeviceContext device, out string reason)
    {
        if (!TryGetDeviceLuid(device, out Luid luid))
        {
            reason = "the Vulkan device reports no LUID";
            return false;
        }

        _dxgi = DXGI.GetApi((Silk.NET.Core.Contexts.INativeWindowSource?)null, false);
        _d3d11 = D3D11.GetApi((Silk.NET.Core.Contexts.INativeWindowSource?)null, false);

        Guid factoryId = IDXGIFactory4.Guid;
        IDXGIFactory4* factory = null;
        int hr = _dxgi.CreateDXGIFactory2(0u, &factoryId, (void**)&factory);
        if (hr < 0)
        {
            reason = $"CreateDXGIFactory2 failed (0x{hr:X8})";
            return false;
        }
        _factory = factory;

        Guid adapterId = IDXGIAdapter.Guid;
        IDXGIAdapter* adapter = null;
        hr = _factory->EnumAdapterByLuid(luid, &adapterId, (void**)&adapter);
        if (hr < 0)
        {
            reason = $"no DXGI adapter matches the Vulkan device LUID (0x{hr:X8})";
            return false;
        }

        D3DFeatureLevel requestedLevel = D3DFeatureLevel.Level111;
        ID3D11Device* d3dDevice = null;
        ID3D11DeviceContext* d3dContext = null;
        hr = _d3d11.CreateDevice(
            adapter,
            D3DDriverType.Unknown,
            0,
            (uint)CreateDeviceFlag.BgraSupport,
            &requestedLevel,
            1,
            D3D11.SdkVersion,
            &d3dDevice,
            null,
            &d3dContext);
        adapter->Release();
        if (hr < 0)
        {
            reason = $"D3D11CreateDevice at feature level 11.1 failed (0x{hr:X8})";
            return false;
        }
        _device = d3dDevice;
        _context = d3dContext;

        Guid device5Id = ID3D11Device5.Guid;
        ID3D11Device5* device5 = null;
        if (_device->QueryInterface(&device5Id, (void**)&device5) < 0)
        {
            reason = "ID3D11Device5 (shared fences) is not available";
            return false;
        }
        _device5 = device5;

        Guid context4Id = ID3D11DeviceContext4.Guid;
        ID3D11DeviceContext4* context4 = null;
        if (_context->QueryInterface(&context4Id, (void**)&context4) < 0)
        {
            reason = "ID3D11DeviceContext4 (fence wait and signal) is not available";
            return false;
        }
        _context4 = context4;

        EnableMultithreadProtection();
        TearingSupported = QueryTearingSupport();
        reason = string.Empty;
        return true;
    }

    private void EnableMultithreadProtection()
    {
        Guid multithreadId = ID3D11Multithread.Guid;
        ID3D11Multithread* multithread = null;
        if (_context->QueryInterface(&multithreadId, (void**)&multithread) < 0)
            return;

        _ = multithread->SetMultithreadProtected(1);
        multithread->Release();
    }

    private bool QueryTearingSupport()
    {
        Guid factory5Id = IDXGIFactory5.Guid;
        IDXGIFactory5* factory5 = null;
        if (_factory->QueryInterface(&factory5Id, (void**)&factory5) < 0)
            return false;

        int allowTearing = 0;
        int hr = factory5->CheckFeatureSupport(Silk.NET.DXGI.Feature.PresentAllowTearing, &allowTearing, sizeof(int));
        factory5->Release();
        return hr >= 0 && allowTearing != 0;
    }

    private static bool TryGetDeviceLuid(VulkanDeviceContext device, out Luid luid)
    {
        luid = default;
        PhysicalDeviceIDProperties idProperties = new()
        {
            SType = StructureType.PhysicalDeviceIDProperties,
        };
        PhysicalDeviceProperties2 properties = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &idProperties,
        };
        device.Api.GetPhysicalDeviceProperties2(device.PhysicalDevice, &properties);
        if (!idProperties.DeviceLuidvalid)
            return false;

        luid = *(Luid*)idProperties.DeviceLuid;
        return true;
    }

    private static void ReleaseCom<T>(ref T* pointer)
        where T : unmanaged
    {
        if (pointer is null)
            return;

        ((IUnknown*)pointer)->Release();
        pointer = null;
    }
}
