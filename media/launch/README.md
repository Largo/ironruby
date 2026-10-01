# IronRuby 4.0 launch video

The source of the launch video. The rendered film lives on the
[v4.0.0 release](https://github.com/Largo/ironruby/releases/tag/v4.0.0) as
`ironruby-4-launch.mp4`, so the repository carries no full renders. What the README and
the release notes show is small: `teaser.webp` plays inline in the README (GitHub does
not play an MP4 there unless it was uploaded through its web editor), and `poster.jpg` is
the title frame.

| File | What it is |
| --- | --- |
| `index.html` | The [HyperFrames](https://hyperframes.heygen.com) composition: 1920x1080, 55 s, nine scenes on one paused GSAP timeline |
| `assets/fonts/` | Inter and JetBrains Mono (SIL Open Font License, the licences beside them), so a render needs no network for fonts |
| `assets/images/notepad.png` | [`Samples/Notepad`](../../Samples/Notepad) editing its own source, captured on Windows 11 at 200 % |
| `poster.jpg` | The title frame with a play button |
| `teaser.webp` | 23 s of the film at 960x540 and 10 fps: the title, the irb session, .NET and the Notepad, the end card. Frames seeked from `index.html` in Chrome, encoded with ImageMagick (`magick -delay 10 -loop 0 f*.png -resize 960x540 -quality 72 teaser.webp`), 1.3 MB |

Every figure on screen comes from the project README (ruby/spec counts, benchmark ratios,
versions), and every line of code runs on IronRuby 4.0. Change the README, change the film.

## Render it

Node 22 and FFmpeg; HyperFrames fetches its own pinned Chrome. The HyperFrames version is
pinned in `package.json`.

```sh
cd media/launch
npm run lint
npm run check      # also audits layout and contrast in the browser
npm run render     # renders/ironruby-4-launch.mp4, 60 fps
npm run preview    # the HyperFrames studio, to scrub and edit
```

`.github/workflows/release.yml` does the same on Ubuntu for every `v*` tag (and for pull
requests that touch this folder) and attaches the MP4 to the release.

The film is silent: it is meant to play muted in a feed.
