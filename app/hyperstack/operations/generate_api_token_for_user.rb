class GenerateAPITokenForUser < Hyperstack::ServerOp
  param :acting_user  
  step {
    params.acting_user.generate_api_token 
    params.acting_user.save
  }  
end
