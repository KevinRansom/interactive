﻿// Copyright(c).NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Linq;

namespace Microsoft.DotNet.Interactive.Formatting
{
    internal static class DefaultTabularDataFormatterSet
    {
        internal static readonly ITypeFormatter[] DefaultFormatters =
            {
                new TabularDataResourceFormatter<IEnumerable>((context, source, writer) =>
                {
                    var tabularData = source.Cast<object>().ToTabularDataResourceJsonString();
                    writer.Write(tabularData.ToString());
                    return true;
                }),

                new TabularDataResourceFormatter<TabularDataResource>((context, source, writer) =>
                {
                    var tabularData = source.ToJson();
                    writer.Write(tabularData);
                    return true;
                }),
            };
    }
}
