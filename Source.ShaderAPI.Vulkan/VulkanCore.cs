using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

using Source.Common.Commands;
using Source.Common.Launcher;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Owns the VkInstance, physical/logical device and queues. Phase 2 of VULKAN_TODO.md:
/// bring the device up far enough to clear the screen; IShaderAPI comes later.
/// </summary>
public unsafe class VulkanCore : IDisposable
{
	// Validation on by default while the backend is under construction; flip the default
	// once the backend is stable.
	static readonly ConVar vk_validation = new("1", 0, "Enable Vulkan validation layers (requires the Vulkan SDK / a validation-capable ICD).");

	public Vk Vk { get; private set; } = null!;
	public Instance Instance { get; private set; }
	public PhysicalDevice PhysicalDevice { get; private set; }
	public Device Device { get; private set; }
	public Queue GraphicsQueue { get; private set; }
	public Queue PresentQueue { get; private set; }
	public uint GraphicsQueueFamily { get; private set; }
	public uint PresentQueueFamily { get; private set; }
	public SurfaceKHR Surface { get; private set; }
	/// <summary>Human-readable "(device name), Vulkan (api version)" - set once a device is picked.</summary>
	public string DeviceDescription { get; private set; } = "Vulkan (no device)";
	public KhrSurface KhrSurface { get; private set; } = null!;
	public KhrSwapchain KhrSwapchain { get; private set; } = null!;

	ExtDebugUtils? debugUtils;
	DebugUtilsMessengerEXT debugMessenger;
	bool validationEnabled;

	readonly IGraphicsProvider provider;

	public VulkanCore(IGraphicsProvider provider) {
		this.provider = provider;
	}

	public bool Init(IWindow window) {
		Vk = Vk.GetApi();

		if (!CreateInstance())
			return false;

		nint surfaceHandle = provider.CreateVulkanSurface((nint)Instance.Handle, window);
		if (surfaceHandle == 0) {
			Warning("Vulkan: surface creation failed\n");
			return false;
		}
		Surface = new SurfaceKHR((ulong)surfaceHandle);

		if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khrSurface)) {
			Warning("Vulkan: VK_KHR_surface not available\n");
			return false;
		}
		KhrSurface = khrSurface;

		if (!PickPhysicalDevice())
			return false;
		if (!CreateLogicalDevice())
			return false;

		return true;
	}

	bool CreateInstance() {
		validationEnabled = vk_validation.GetBool() && HasLayer("VK_LAYER_KHRONOS_validation");
		if (vk_validation.GetBool() && !validationEnabled)
			Warning("Vulkan: validation requested but VK_LAYER_KHRONOS_validation is not installed\n");

		string[] windowExtensions = provider.GetVulkanInstanceExtensions();
		if (windowExtensions.Length == 0) {
			Warning("Vulkan: window system reports no instance extensions\n");
			return false;
		}

		List<string> extensions = [.. windowExtensions];
		if (validationEnabled)
			extensions.Add(ExtDebugUtils.ExtensionName);

		byte* appName = (byte*)SilkMarshal.StringToPtr("Source.NET");
		ApplicationInfo appInfo = new() {
			SType = StructureType.ApplicationInfo,
			PApplicationName = appName,
			ApplicationVersion = new Version32(1, 0, 0),
			PEngineName = appName,
			EngineVersion = new Version32(1, 0, 0),
			ApiVersion = Vk.Version13
		};

		byte** extensionsPtr = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray());
		string[] layers = validationEnabled ? ["VK_LAYER_KHRONOS_validation"] : [];
		byte** layersPtr = layers.Length > 0 ? (byte**)SilkMarshal.StringArrayToPtr(layers) : null;

		InstanceCreateInfo createInfo = new() {
			SType = StructureType.InstanceCreateInfo,
			PApplicationInfo = &appInfo,
			EnabledExtensionCount = (uint)extensions.Count,
			PpEnabledExtensionNames = extensionsPtr,
			EnabledLayerCount = (uint)layers.Length,
			PpEnabledLayerNames = layersPtr
		};

		DebugUtilsMessengerCreateInfoEXT debugCreateInfo = default;
		if (validationEnabled) {
			PopulateDebugCreateInfo(ref debugCreateInfo);
			createInfo.PNext = &debugCreateInfo;
		}

		Result result = Vk.CreateInstance(&createInfo, null, out Instance instance);

		SilkMarshal.Free((nint)extensionsPtr);
		if (layersPtr != null) SilkMarshal.Free((nint)layersPtr);
		SilkMarshal.Free((nint)appName);

		if (result != Result.Success) {
			Warning($"Vulkan: vkCreateInstance failed ({result})\n");
			return false;
		}
		Instance = instance;

		if (validationEnabled && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils du)) {
			debugUtils = du;
			DebugUtilsMessengerCreateInfoEXT messengerInfo = default;
			PopulateDebugCreateInfo(ref messengerInfo);
			if (debugUtils.CreateDebugUtilsMessenger(Instance, &messengerInfo, null, out debugMessenger) != Result.Success)
				Warning("Vulkan: could not create debug messenger\n");
		}

		Msg($"Vulkan: instance created (validation {(validationEnabled ? "on" : "off")})\n");
		return true;
	}

	bool HasLayer(string layerName) {
		uint count = 0;
		Vk.EnumerateInstanceLayerProperties(&count, null);
		LayerProperties[] layers = new LayerProperties[count];
		fixed (LayerProperties* layersPtr = layers)
			Vk.EnumerateInstanceLayerProperties(&count, layersPtr);
		foreach (ref readonly LayerProperties layer in layers.AsSpan()) {
			fixed (byte* namePtr = layer.LayerName) {
				if (SilkMarshal.PtrToString((nint)namePtr) == layerName)
					return true;
			}
		}
		return false;
	}

	static void PopulateDebugCreateInfo(ref DebugUtilsMessengerCreateInfoEXT info) {
		info.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
		info.MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
		info.MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt;
		info.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
	}

	static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT types, DebugUtilsMessengerCallbackDataEXT* data, void* userData) {
		string message = SilkMarshal.PtrToString((nint)data->PMessage) ?? "<null>";
		if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
			Warning($"Vulkan validation ERROR: {message}\n");
		else
			Warning($"Vulkan validation: {message}\n");
		return Vk.False;
	}

	bool PickPhysicalDevice() {
		uint deviceCount = 0;
		Vk.EnumeratePhysicalDevices(Instance, &deviceCount, null);
		if (deviceCount == 0) {
			Warning("Vulkan: no physical devices\n");
			return false;
		}
		PhysicalDevice[] devices = new PhysicalDevice[deviceCount];
		fixed (PhysicalDevice* devicesPtr = devices)
			Vk.EnumeratePhysicalDevices(Instance, &deviceCount, devicesPtr);

		PhysicalDevice best = default;
		int bestScore = -1;
		uint bestGraphics = 0, bestPresent = 0;
		foreach (PhysicalDevice candidate in devices) {
			if (!FindQueueFamilies(candidate, out uint graphicsFamily, out uint presentFamily))
				continue;
			if (!SupportsRequiredExtensions(candidate))
				continue;
			if (!SupportsRequiredFeatures(candidate))
				continue;

			Vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties props);
			int score = props.DeviceType switch {
				PhysicalDeviceType.DiscreteGpu => 100,
				PhysicalDeviceType.IntegratedGpu => 50,
				_ => 10
			};
			if (score > bestScore) {
				best = candidate;
				bestScore = score;
				bestGraphics = graphicsFamily;
				bestPresent = presentFamily;
			}
		}

		if (bestScore < 0) {
			Warning("Vulkan: no suitable physical device (need graphics+present queues, swapchain, Vulkan 1.3 with dynamic rendering + sync2)\n");
			return false;
		}

		PhysicalDevice = best;
		GraphicsQueueFamily = bestGraphics;
		PresentQueueFamily = bestPresent;

		Vk.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties chosen);
		uint api = chosen.ApiVersion;
		DeviceDescription = $"{SilkMarshal.PtrToString((nint)chosen.DeviceName)}, Vulkan {api >> 22}.{(api >> 12) & 0x3FF}.{api & 0xFFF}";
		Msg($"Vulkan: using {DeviceDescription}\n");
		return true;
	}

	bool FindQueueFamilies(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily) {
		graphicsFamily = presentFamily = 0;
		bool foundGraphics = false, foundPresent = false;

		uint count = 0;
		Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
		QueueFamilyProperties[] families = new QueueFamilyProperties[count];
		fixed (QueueFamilyProperties* familiesPtr = families)
			Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, familiesPtr);

		for (uint i = 0; i < count; i++) {
			if (!foundGraphics && (families[i].QueueFlags & QueueFlags.GraphicsBit) != 0) {
				graphicsFamily = i;
				foundGraphics = true;
			}
			KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out Bool32 presentSupport);
			if (!foundPresent && presentSupport) {
				presentFamily = i;
				foundPresent = true;
			}
			// Prefer a single family that does both
			if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0 && presentSupport) {
				graphicsFamily = presentFamily = i;
				return true;
			}
		}
		return foundGraphics && foundPresent;
	}

	bool SupportsRequiredExtensions(PhysicalDevice device) {
		uint count = 0;
		Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
		ExtensionProperties[] extensions = new ExtensionProperties[count];
		fixed (ExtensionProperties* extensionsPtr = extensions)
			Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, extensionsPtr);
		foreach (ref readonly ExtensionProperties ext in extensions.AsSpan()) {
			fixed (byte* namePtr = ext.ExtensionName) {
				if (SilkMarshal.PtrToString((nint)namePtr) == KhrSwapchain.ExtensionName)
					return true;
			}
		}
		return false;
	}

	bool SupportsRequiredFeatures(PhysicalDevice device) {
		PhysicalDeviceVulkan13Features features13 = new() { SType = StructureType.PhysicalDeviceVulkan13Features };
		PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features13 };
		Vk.GetPhysicalDeviceFeatures2(device, &features2);
		return features13.DynamicRendering && features13.Synchronization2;
	}

	bool CreateLogicalDevice() {
		float priority = 1.0f;
		uint[] uniqueFamilies = GraphicsQueueFamily == PresentQueueFamily
			? [GraphicsQueueFamily]
			: [GraphicsQueueFamily, PresentQueueFamily];

		DeviceQueueCreateInfo* queueInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
		for (int i = 0; i < uniqueFamilies.Length; i++) {
			queueInfos[i] = new DeviceQueueCreateInfo {
				SType = StructureType.DeviceQueueCreateInfo,
				QueueFamilyIndex = uniqueFamilies[i],
				QueueCount = 1,
				PQueuePriorities = &priority
			};
		}

		PhysicalDeviceVulkan13Features features13 = new() {
			SType = StructureType.PhysicalDeviceVulkan13Features,
			DynamicRendering = true,
			Synchronization2 = true
		};
		PhysicalDeviceFeatures2 features2 = new() {
			SType = StructureType.PhysicalDeviceFeatures2,
			PNext = &features13
		};

		byte** extensionsPtr = (byte**)SilkMarshal.StringArrayToPtr(new[] { KhrSwapchain.ExtensionName });

		DeviceCreateInfo createInfo = new() {
			SType = StructureType.DeviceCreateInfo,
			PNext = &features2,
			QueueCreateInfoCount = (uint)uniqueFamilies.Length,
			PQueueCreateInfos = queueInfos,
			EnabledExtensionCount = 1,
			PpEnabledExtensionNames = extensionsPtr
		};

		Result result = Vk.CreateDevice(PhysicalDevice, &createInfo, null, out Device device);
		SilkMarshal.Free((nint)extensionsPtr);

		if (result != Result.Success) {
			Warning($"Vulkan: vkCreateDevice failed ({result})\n");
			return false;
		}
		Device = device;

		Vk.GetDeviceQueue(Device, GraphicsQueueFamily, 0, out Queue graphicsQueue);
		Vk.GetDeviceQueue(Device, PresentQueueFamily, 0, out Queue presentQueue);
		GraphicsQueue = graphicsQueue;
		PresentQueue = presentQueue;

		if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khrSwapchain)) {
			Warning("Vulkan: VK_KHR_swapchain not available on device\n");
			return false;
		}
		KhrSwapchain = khrSwapchain;
		return true;
	}

	public void Dispose() {
		if (Device.Handle != 0) {
			Vk.DeviceWaitIdle(Device);
			Vk.DestroyDevice(Device, null);
		}
		if (Surface.Handle != 0)
			provider.DestroyVulkanSurface((nint)Instance.Handle, (nint)Surface.Handle);
		if (debugUtils != null && debugMessenger.Handle != 0)
			debugUtils.DestroyDebugUtilsMessenger(Instance, debugMessenger, null);
		if (Instance.Handle != 0)
			Vk.DestroyInstance(Instance, null);
	}
}
