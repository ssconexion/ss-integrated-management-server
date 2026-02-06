# Spanish Showdown Integrated Management Server
No quieres depender de refs que aparecen cuando les da la gana y te tienes que acabar mamando 4 matches simultáneas? Desde SSConexión proponemos algo nuevo; un AutoRef con integración en discord para lobbys de qualifiers y fase eliminatoria normal. Pensado principalmente para el flow de matches de la Spanish Showdown. 

## Cómo usar
Para poder hacer uso de este programa, debes tener un servidor de **PostgreSQL** funcional y** rellenar el .env** con todo lo necesario. Despues, debes hacer lo siguiente: 
- `dotnet ef migrations add InitialCreate`
- `dotnet ef database update` 

Si solo vas a usar este programa en versión stand-alone, comenta las líneas de IgnoreMigrations en `ModelsContext.cs`, por que si no, no te va a crear las tablas ya que este Manager esta pensado para que funcione junto a una base de datos vinculada a la web oficial del torneo. 
