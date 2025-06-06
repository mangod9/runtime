// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Extensions.Configuration.Ini;
using Microsoft.Extensions.FileProviders;

namespace Microsoft.Extensions.Configuration
{
    /// <summary>
    /// Provides extension methods for adding <see cref="IniConfigurationProvider"/>.
    /// </summary>
    public static class IniConfigurationExtensions
    {
        /// <summary>
        /// Adds the INI configuration provider at <paramref name="path"/> to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="path">The path relative to the base path stored in
        /// <see cref="IConfigurationBuilder.Properties"/> of <paramref name="builder"/>.</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
        public static IConfigurationBuilder AddIniFile(this IConfigurationBuilder builder, string path)
        {
            return AddIniFile(builder, provider: null, path: path, optional: false, reloadOnChange: false);
        }

        /// <summary>
        /// Adds the INI configuration provider at <paramref name="path"/> to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="path">The path relative to the base path stored in
        /// <see cref="IConfigurationBuilder.Properties"/> of <paramref name="builder"/>.</param>
        /// <param name="optional"><see langword="true"/> if the file is optional; otherwise, <see langword="false"/> .</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
        public static IConfigurationBuilder AddIniFile(this IConfigurationBuilder builder, string path, bool optional)
        {
            return AddIniFile(builder, provider: null, path: path, optional: optional, reloadOnChange: false);
        }

        /// <summary>
        /// Adds the INI configuration provider at <paramref name="path"/> to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="path">Path relative to the base path stored in
        /// <see cref="IConfigurationBuilder.Properties"/> of <paramref name="builder"/>.</param>
        /// <param name="optional"><see langword="true"/> if the file is optional; otherwise, <see langword="false"/>.</param>
        /// <param name="reloadOnChange"><see langword="true"/> if the configuration should be reloaded if the file changes; otherwise, <see langword="false"/>.</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
        public static IConfigurationBuilder AddIniFile(this IConfigurationBuilder builder, string path, bool optional, bool reloadOnChange)
        {
            return AddIniFile(builder, provider: null, path: path, optional: optional, reloadOnChange: reloadOnChange);
        }

        /// <summary>
        /// Adds a INI configuration source to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="provider">The <see cref="IFileProvider"/> to use to access the file.</param>
        /// <param name="path">Path relative to the base path stored in
        /// <see cref="IConfigurationBuilder.Properties"/> of <paramref name="builder"/>.</param>
        /// <param name="optional"><see langword="true"/> if the file is optional; otherwise, <see langword="false"/>.</param>
        /// <param name="reloadOnChange"><see langword="true"/> if the configuration should be reloaded if the file changes; otherwise, <see langword="false"/>.</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
        public static IConfigurationBuilder AddIniFile(this IConfigurationBuilder builder, IFileProvider? provider, string path, bool optional, bool reloadOnChange)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException(SR.Error_InvalidFilePath, nameof(path));
            }

            return builder.AddIniFile(s =>
            {
                s.FileProvider = provider;
                s.Path = path;
                s.Optional = optional;
                s.ReloadOnChange = reloadOnChange;
                s.ResolveFileProvider();
            });
        }

        /// <summary>
        /// Adds a INI configuration source to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="configureSource">Configures the source.</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
        public static IConfigurationBuilder AddIniFile(this IConfigurationBuilder builder, Action<IniConfigurationSource>? configureSource)
            => builder.Add(configureSource);

        /// <summary>
        /// Adds a INI configuration source to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="stream">The <see cref="Stream"/> to read the INI configuration data from.</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
        public static IConfigurationBuilder AddIniStream(this IConfigurationBuilder builder, Stream stream)
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Add<IniStreamConfigurationSource>(s => s.Stream = stream);
        }

    }
}
