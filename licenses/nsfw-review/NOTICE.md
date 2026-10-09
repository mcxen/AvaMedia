# NSFW review vocabularies

`nsfw-review.tsv` is an AvaMedia review subset and Chinese mapping of the JoyTag
Danbooru-style vocabulary. Tag names originate from `fancyfeast/joytag` revision
`6b7f16331a6ccf0fdce37d5a9564715f6e772b22`, licensed Apache-2.0. The original
vocabulary, weight hashes and license are retained in `licenses/joytag/`.
Review groups, Chinese labels and the risk/context policy are AvaMedia additions;
they are not an upstream NSFW classifier or a calibrated probability.

Additional adult-detail Chinese labels in `joytag-zh-curated.tsv` are maintained
by AvaMedia and extend browsing/display candidates, not the 93 automatic-review
rules. The general-label Chinese fallback and its MIT notice are documented in
`licenses/chinese-tags/`.

- Upstream: https://github.com/fpgaminer/joytag
- Pinned labels: https://huggingface.co/fancyfeast/joytag/blob/6b7f16331a6ccf0fdce37d5a9564715f6e772b22/top_tags.txt

`nudenet-labels.tsv` adapts the 18 class names in the NudeNet README at commit
`6ccc81c6c305cccfd46d92b414f8a5c0a816574d`, with original Chinese translations and
semantic descriptions. NudeNet is published under AGPL-3.0; both upstream license
files are retained alongside this notice. NudeNet code and weights are not
bundled. These classes are semantic review candidates, not JoyTag outputs or
NudeNet detector predictions. Nonsexual anatomy and covered classes are not NSFW
verdicts.

- Upstream: https://github.com/notAI-tech/NudeNet
- Class definitions: https://github.com/notAI-tech/NudeNet/blob/6ccc81c6c305cccfd46d92b414f8a5c0a816574d/README.md

WD Tagger v3 was reviewed as an alternative. It includes rating outputs but is
trained on Danbooru images; its separate vocabulary and rating outputs are not
represented as JoyTag capabilities. No WD files are bundled.

Research sources were inspected on 2026-10-08. Only vocabularies with identified
upstream provenance and license are included.
