﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DotNet.Interactive.LanguageService
{
    public class HoverParams
    {
        public TextDocument TextDocument { get; set; }
        public Position Position { get; set; }
    }
}
