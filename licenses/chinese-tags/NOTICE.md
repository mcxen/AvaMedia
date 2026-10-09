# Chinese JoyTag display labels

`src/AvaMedia.Core/Assets/AiLexicons/joytag-zh.tsv` derives the Chinese fallback
names for JoyTag general and metadata tags from Physton's MIT-licensed
`sd-webui-prompt-all-in-one-assets` repository:

- Source: https://github.com/Physton/sd-webui-prompt-all-in-one-assets
- Revision: `6dd9e03e8f8b9a0f7f4790bbaf1833c029e63b14`
- File: `tags/danbooru.zh_CN.csv`
- Original CSV SHA-256: `79c24445ea5cc85d94c0664d13a411fc292b0637f75b1e8e45e5dcffaf5e55d5`
- Copyright (c) 2023 Physton; the MIT license is retained in `LICENSE.txt`.

The bundled subset intersects JoyTag's pinned 5813-tag vocabulary. Only general
and metadata translations are selected; artist, character and franchise names
retain their original names. Tag categories were consulted from
https://github.com/DominikDoom/a1111-sd-webui-tagcomplete/blob/4170882f90b47be130a0ff9314f663c230b9153d/tags/danbooru.csv
for that selection; its CSV is not bundled. Only the first Chinese translation
term is retained, with filename-unsafe punctuation removed. Existing AvaMedia
mappings and corrected names override the fallback translations.

`joytag-zh-curated.tsv` contains 421 AvaMedia-authored Chinese labels and categories,
including 117 outdoor scenery entries and 231 additional adult-content detail
entries. These corrections and the existing person/scene/NSFW vocabularies take
precedence over community translations. Labels are display names for existing
model outputs, not new model classes or accuracy claims. Supplemental adult
labels do not add rules to `NsfwModeration` or alter the binary classifier.
