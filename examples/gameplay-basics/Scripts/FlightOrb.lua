-- FlightOrb.lua: a trigger; touching it starts flight for whoever has a Flight script.
function OnTriggerEnter(other)
  local flight = other and other:GetScript("Flight")
  if flight and not flight.IsFlying() then
    flight.SetFlying(true)
    print("ORB flight on")
    Events.Emit("Hint", "Flying! W/A/S/D, Space up, LeftControl down, G or the Fly button lands")
  end
end
