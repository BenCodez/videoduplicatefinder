// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;

namespace VDF.Core.Nfo;

/// <summary>
/// NFO protection evidence. Callers must also protect uncertain results from
/// automatic removal until the metadata can be read successfully.
/// </summary>
public sealed record NfoResult(bool IsFavourite, bool IsUncertain, string Reason, IReadOnlyList<string> Sources);
