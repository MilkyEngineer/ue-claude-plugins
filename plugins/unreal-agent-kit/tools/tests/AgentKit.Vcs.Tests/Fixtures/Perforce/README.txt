Perforce responses in `p4 -ztag` text form, one "... tag value" line per field and a blank line between records.
The tests encode them to `p4 -G` (Python marshal) bytes and feed them to EpicGames.Perforce's own parser through a fake
IPerforceConnection. {root} is replaced with the test's client root. The fields and values follow real p4 2024.x output;
error records carry `severity` and `generic` as integers, as p4 -G does.
