# Procedural Level Generation

This selection of scripts are responsible for procedural level generation. The level is populated with rooms whose layouts are regular polygons and are aligned side-by-side in a tidy manner. The rooms are then connected to each other, forming a bigger complex. The system allows the designers to configure the level generations, such as the number of rooms, the largetest possible polygon, and the height discrepancy between levels, and more.

[PolygonRoom](https://github.com/BenWeiTang/Galactic-Gauntlet-Code-Snippets/blob/main/ProceduralLevel/PolygonRoom.cs) is a class responsible for anchoring and determining the type of polygon the room is built upon.

[PolygonPort](https://github.com/BenWeiTang/Galactic-Gauntlet-Code-Snippets/blob/main/ProceduralLevel/PolygonPort.cs) facilitates the connection between neighboring roomz.

[ProceduralLevelManager](https://github.com/BenWeiTang/Galactic-Gauntlet-Code-Snippets/blob/main/ProceduralLevel/ProceduralLevelManager.cs) takes care of the random generation and building the rooms, which uses Unity ProBuilder scripting API. It also communicates the info regarding level generation across the network by sending a seed, so that each machine can independently construct the complex while making sure the complex on each machine is identical.

See [demo](https://www.artstation.com/artwork/03Z158) for more info.
