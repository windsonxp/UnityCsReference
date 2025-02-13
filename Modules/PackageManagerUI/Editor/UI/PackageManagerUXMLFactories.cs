// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using UnityEngine.UIElements;

namespace UnityEditor.PackageManager.UI
{
    [InitializeOnLoad]
    internal class PackageManagerUXMLFactories
    {
        private static readonly bool k_Registered;

        static PackageManagerUXMLFactories()
        {
            if (k_Registered)
                return;

            k_Registered = true;

            IUxmlFactory[] factories =
            {
                new Alert.UxmlFactory(),
                new ArrowToggle.UxmlFactory(),
                new LoadingSpinner.UxmlFactory(),
                new PackageDependencies.UxmlFactory(),
                new PackageDetails.UxmlFactory(),
                new PackageItem.UxmlFactory(),
                new PackageList.UxmlFactory(),
                new PackageManagerToolbar.UxmlFactory(),
                new PackageSampleList.UxmlFactory(),
                new PackageStatusBar.UxmlFactory(),
                new PackageToolbar.UxmlFactory(),
                new DropdownButton.UxmlFactory(),
            };

            foreach (IUxmlFactory factory in factories)
            {
                VisualElementFactoryRegistry.RegisterFactory(factory);
            }
        }
    }
}
