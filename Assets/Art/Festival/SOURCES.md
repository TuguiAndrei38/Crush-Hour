# Summer Music Festival model sources

Retrieved 5 November 2026 from the links selected by the project owner.

## 1DesignTool Music Festival collection

Publisher: StoicSoft / 1DesignTool. The publisher identifies these models as
AI-generated. The six models share a cream, ochre, charcoal, timber and green
palette. They have no image textures and use flat-shaded geometry.

Official collection: <https://1designtool.com/3d/collections/music-festival>

Official licence: <https://1designtool.com/3d/license>

The publisher allows personal and commercial game use, modification and
shipping the models as part of a game, without required attribution. Its
licence prohibits reselling or redistributing the original or modified models
as standalone model files, model packs or asset libraries. This is a custom
licence, not CC0. Check those restrictions before making source model files
available outside the project team.

Original GLBs, preview images and reference pages are retained locally under
`Tools~/Art/FestivalSource/1DesignTool/`. They are source material, not runtime
downloads. The Unity assets are converted copies with shared URP materials.

- **Wristband booth**
  - Page: <https://1designtool.com/3d/models/music-festival-wristband-booth>
  - GLB: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-wristband-booth/93fd9c2ca1/model.glb>
  - Preview: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-wristband-booth/93fd9c2ca1/poster.webp>
  - Actual bounds: 2.86 m wide, 3.585 m tall, 2.933 m deep.
  - A reception or merchandise kiosk. The original rooftop wristband symbol is
    suitable for a bracelet-merch stand; it is separate geometry if changed.

- **Lights**
  - Page: <https://1designtool.com/3d/models/music-festival-lights>
  - GLB: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-lights/1fc717409c/model.glb>
  - Preview: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-lights/1fc717409c/poster.webp>
  - Actual full-unit bounds: 3.655 m wide, 6 m tall, 2.54 m deep.
  - The portable tower includes separate mast, head, lamps, generator, trailer,
    wheel and outrigger groups. The narrow upper tower can be adapted to the
    existing collapsible light-tower footprint. The generator group is also a
    useful source for the backstage generator model.

- **Small stage**
  - Page: <https://1designtool.com/3d/models/music-festival-small-stage>
  - GLB: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-small-stage/afb834d26e/model.glb>
  - Preview: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-small-stage/afb834d26e/poster.webp>
  - Actual bounds: 10.5 m wide, 6.67 m tall, 6.763 m deep.
  - Retain its proportions when dressing the larger stage footprint. The roof,
    trusses, stage equipment and speaker groups are separate source nodes.

- **Speakers**
  - Page: <https://1designtool.com/3d/models/music-festival-speakers>
  - GLB: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-speakers/476fe1eb95/model.glb>
  - Preview: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-speakers/476fe1eb95/poster.webp>
  - Actual bounds: 1.16 m wide, 2.386 m tall, 1.018 m deep.
  - Separate PA stacks can dress the stage and sound-control area.

- **Food row**
  - Page: <https://1designtool.com/3d/models/music-festival-food-row>
  - GLB: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-food-row/c1f60aadb6/model.glb>
  - Preview: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-food-row/c1f60aadb6/poster.webp>
  - Actual full-row bounds: 10.71 m wide, 4.23 m tall, 3.45 m deep.
  - The `foodrow_stalls` group contains independent `gazebo`, `shack` and
    `trailer` groups. Those can populate the existing individual food plots;
    the separate `foodrow_lights` group is optional decoration.
  - Extracted dimensions: gazebo 2.744 x 3.895 x 2.344 m; shack 2.66 x 3.191 x
    3.186 m; trailer 3.55 x 4.23 x 2.591 m (width x height x depth).

- **Portable toilets**
  - Page: <https://1designtool.com/3d/models/music-festival-portable-toilets>
  - GLB: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-portable-toilets/e7290a7bb3/model.glb>
  - Preview: <https://assets.stoicsoft.com/1designtool/3d/models/music-festival-portable-toilets/e7290a7bb3/poster.webp>
  - Actual bounds: 1.24 m wide, 2.52 m tall, 1.364 m deep.
  - Repeat cabins to form the existing toilet-bank footprint.

Dimensions above were checked from the downloaded GLB geometry with node
transforms applied. They are not target dimensions to stretch each asset to.
Models use metres, Y up and a front facing positive Z in their original files.

## DesignerSoup food trucks

Selected source page: <https://designersoup.itch.io/low-poly-foodtrucks>

The page allows free commercial and non-commercial game use and modification;
credit is appreciated but not required. It prohibits redistribution or resale
as standalone models or asset packs. This licence is separate from the
1DesignTool licence. The publisher states that no generative AI was used.

Downloaded archive: `designersoups-low-poly-foodtrucks.zip`, retained locally
under `Tools~/Art/FestivalSource/FoodTrucks/`.

Original archive contents used:

- `designersoups low poly foodtrucks/burger truck.fbx`
- `designersoups low poly foodtrucks/donut truck.fbx`
- A shared palette image named `387359c5580f06c08c266126b3b46db47e48ba44.png`
  inside each truck's `.fbm` directory.

The two food-service vehicles suit the festival food court. The original
materials use a palette texture; the import retains that atlas and its UVs,
alongside shared plain-colour URP materials. Archive metadata such as `__MACOSX` files is not
game content.

## Gameplay integration

The festival grid, obstacle footprints, doors, fire sites, collapse cells and
crowd behaviour remain authoritative. Art is fitted within those footprints;
visible detail does not create new simulation obstacles. Source per-mesh
material duplicates should be consolidated into shared palette materials.
