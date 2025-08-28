# build-common
Common build targets and scripts necessary for libraries and components 

## What's included

1. `Common.props` file necessary to be included from your root `Directory.Build.props`
2. Shared `.target` files in `targets/` directory. Includes NullableEnable, TrimmingEnable, SignEnable, PackEnable and more.
   
    These targets can be referenced with `$(RepositoryPropsRoot)` prefix

    ```xml
    <Import Project="$(RepositoryPropsRoot)/NullableEnable.targets" />
    ```

3. Nuke extension, including BuildToLocalCache and simple obfuscation/ilmerge wrappers
4. Avalonia strong name key and public key
4. Babel obfuscator license file
5. Some branding images, like default nuget package icon