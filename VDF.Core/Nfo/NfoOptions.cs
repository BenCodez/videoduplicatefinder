// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;

namespace VDF.Core.Nfo;

/// <summary>Explicit local XML markers that protect media as favourites.</summary>
public sealed class NfoOptions {
	public bool Enabled { get; set; }
	public List<string> BooleanElements { get; set; } = ["favorite", "isfavorite"];
	public List<string> TagValues { get; set; } = ["Favorite", "Favourite"];
	public List<string> TagElements { get; set; } = ["tag"];
	public bool InheritSeries { get; set; }
	public bool InheritSeason { get; set; }
}
