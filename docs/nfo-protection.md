# Local NFO favourite protection

NFO scanning is disabled by default. Enabling it detects explicit markers in local
XML files; it does not connect to Emby. User-specific Emby favourites are usually
stored separately from NFO metadata, so test representative files with the NFO
preview before relying on a custom format.

The default positive markers are:

```xml
<favorite>true</favorite>
<isfavorite>true</isfavorite>
<tag>Favorite</tag>
<tag>Favourite</tag>
```

Element names and tag values are configurable and match without regard to case.
Surrounding whitespace is ignored. Boolean elements accept `true` or `1` as
positive, and `false` or `0` as negative. A positive anywhere in applicable NFOs
wins over a negative. Ratings, user ratings, watched status, titles, and comments
do not imply a favourite. Elements with nested XML do not count as boolean or tag
text markers. Namespace prefixes do not change matching of an element's local
name.

## Associations

- A video uses `.nfo` files with the same basename in its directory. `.nfo`
  extension matching ignores case; basename matching follows the platform's path
  comparison rules. Episode sidecars work in directories containing many videos.
- Shared `movie.nfo` applies only when the directory contains exactly one video
  recognized by VDF, and it is the video being inspected. Videos in subdirectories
  do not affect this count. A video literally named `movie.mkv` can still use its
  exact `movie.nfo` sidecar when other videos exist.
- Series inheritance is optional. It uses the nearest `tvshow.nfo` in the video's
  directory or up to eight parent directories. A nearer series boundary prevents
  inheritance from an unrelated outer series.
- Season inheritance is optional. It uses the nearest `season.nfo` in the same
  bounded search, and stops at a `tvshow.nfo` boundary. This permits episode
  subdirectories without inheriting a season from outside the series.

Inherited negative markers do not cancel positive episode or other applicable
markers. Every positively marked duplicate remains protected.

## Diagnostics and safety

`NfoFavouriteService.Inspect(videoPath, options)` returns `NfoResult` with
`IsFavourite`, `IsUncertain`, `Reason`, and `Sources`. Sources contains the full
paths of applicable NFOs actually inspected. Reasons explain positive, negative,
unrecognized, missing, ambiguous, and unreadable metadata.

Automatic selection and cleanup must protect a file when either `IsFavourite` or
`IsUncertain` is true. Malformed XML, unavailable directories, inaccessible NFOs,
and inspection limits produce uncertain results. An already observed positive
remains positive if malformed XML is encountered later in the same file.

The parser prohibits DTDs and external entities, uses a null XML resolver, validates
XML characters, and honors XML encoding declarations. It does not rewrite NFOs.
Each NFO is limited to 1 MiB, XML nesting to 64 levels, each directory listing to
100,000 entries, and each inspection to 32 applicable NFOs. Hitting a limit blocks
automatic removal through the uncertain result.

## Cache and rescans

Each inspection reads bounded NFO bytes and computes a SHA-256 hash. Parsed XML
is reused only when both the marker configuration and content hash match. This
detects edits with unchanged length and modification timestamp, including changes
to shared series metadata.

Directory associations are indexed once and reused while the directory timestamp
is unchanged; same-basename lookups do not rescan the list for every video. The
bounded cache holds at most 2,048 directory snapshots and 4,096 parsed results,
with a separate budget for cached sidecar paths.

Call `ClearCache()` before a fresh bulk rescan or selection evaluation when a
network mount or other filesystem may preserve directory timestamps. This is
necessary to discover added sidecars and changed movie associations on such
mounts. Content changes to already discovered NFO files are always hash-validated,
even without clearing the cache.
