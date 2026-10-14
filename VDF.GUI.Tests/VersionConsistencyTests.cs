// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Text.RegularExpressions;

using VDF.GUI.Utils;

namespace VDF.GUI.Tests;

/// <summary>
/// The GUI assembly must report the release line declared in Directory.Build.props.
/// Publication-policy tests separately validate selected version tags against that prefix.
/// </summary>
public class VersionConsistencyTests {

	static string RepoRoot() {
		// Walk up from the test bin folder to the repo root.
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
			dir = dir.Parent;
		Assert.NotNull(dir);
		return dir!.FullName;
	}

	static string VersionPrefixMajorMinor() {
		string props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
		var match = Regex.Match(props, @"<VersionPrefix>(\d+)\.(\d+)\.\d+</VersionPrefix>");
		Assert.True(match.Success, "Directory.Build.props: no <VersionPrefix> found");
		return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
	}

	[Fact]
	public void BuiltAssemblyVersion_MatchesVersionPrefix() {
		// VersionInfo.Version prefers the entry assembly, which under the test host is
		// testhost rather than the GUI — check the VDF.GUI assembly directly instead.
		var v = typeof(VersionInfo).Assembly.GetName().Version;
		Assert.NotNull(v);
		Assert.Equal(VersionPrefixMajorMinor(), $"{v!.Major}.{v.Minor}");
	}
}
